<#
.SYNOPSIS
    Registers (or removes) a Windows scheduled task so tenant-pulse keeps the tenant alive daily.

.DESCRIPTION
    The simulator already rolls over at midnight UTC and re-plans the next day, so "daily activity"
    only needs the process to be running. This makes that survive a reboot.

    It must run as YOU, not as SYSTEM or a service account: each user's MSAL token cache in
    .state/token-cache is encrypted with DPAPI scoped to the Windows user that enrolled them.
    Another account cannot decrypt it, and the simulator would fall back to re-enrolling every user
    on every start.

    By default the task starts at logon, which needs no stored Windows password. Pass
    -RunWhetherLoggedOn to have it start at boot instead; Windows will then prompt for your Windows
    password so it can log you on in the background.

    A daily repair trigger re-starts the task if it ever died. MultipleInstances=IgnoreNew means a
    second trigger can never produce two simulators posting at once.

.PARAMETER TaskName
    Scheduled task name. Defaults to 'tenant-pulse'.

.PARAMETER RepoRoot
    Repository root. Defaults to the parent of the folder holding this script.

.PARAMETER Configuration
    Build configuration the launcher should run. Defaults to Release.

.PARAMETER RunWhetherLoggedOn
    Start at boot rather than at logon. Prompts for your Windows password so the task can run while
    you are signed out.

.PARAMETER RepairAt
    Time of day for the daily "start it if it isn't running" trigger. Defaults to 06:30.

.PARAMETER Remove
    Unregister the task and do nothing else.

.EXAMPLE
    ./scripts/install-daily-task.ps1

.EXAMPLE
    ./scripts/install-daily-task.ps1 -RunWhetherLoggedOn

.EXAMPLE
    ./scripts/install-daily-task.ps1 -Remove
#>

[CmdletBinding()]
param(
    [string] $TaskName = 'tenant-pulse',
    [string] $RepoRoot,
    [ValidateSet('Debug', 'Release')]
    [string] $Configuration = 'Release',
    [switch] $RunWhetherLoggedOn,
    [string] $RepairAt = '06:30',
    [switch] $Remove
)

$ErrorActionPreference = 'Stop'

if (-not $RepoRoot) {
    $RepoRoot = Split-Path -Parent $PSScriptRoot
}
$RepoRoot = (Resolve-Path $RepoRoot).Path

Write-Host ''
Write-Host 'tenant-pulse - daily scheduled task' -ForegroundColor White
Write-Host ('-' * 60)

if ($Remove) {
    if (Get-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue) {
        Unregister-ScheduledTask -TaskName $TaskName -Confirm:$false
        Write-Host "Removed scheduled task '$TaskName'." -ForegroundColor Green
    }
    else {
        Write-Host "No scheduled task named '$TaskName'." -ForegroundColor Yellow
    }
    return
}

# --- preflight ---------------------------------------------------------------
$launcher = Join-Path $RepoRoot 'scripts/run-daily.ps1'
if (-not (Test-Path $launcher)) {
    throw "Launcher not found at $launcher"
}

$configPath = Join-Path $RepoRoot 'config/tenant-pulse.json'
if (-not (Test-Path $configPath)) {
    throw "No config at $configPath. Run scripts/setup-app-registration.ps1 first."
}

$cacheDir = Join-Path $RepoRoot '.state/token-cache'
$enrolled = if (Test-Path $cacheDir) { @(Get-ChildItem $cacheDir -Filter *.msalcache).Count } else { 0 }
if ($enrolled -eq 0) {
    Write-Warning "No users are enrolled yet (.state/token-cache is empty). Run 'tenant-pulse bootstrap' or the task will have nobody to act as."
}
else {
    Write-Host "  $enrolled user(s) enrolled" -ForegroundColor DarkGray
}

# The password is only the fallback for a dead cache entry, but without it an unattended run cannot
# self-heal - it would need a human to re-enrol.
$config = Get-Content $configPath -Raw | ConvertFrom-Json
$mode = $config.TenantPulse.Auth.Mode
$hasConfigPassword = -not [string]::IsNullOrWhiteSpace($config.TenantPulse.Auth.SharedPassword)
$hasUserEnvPassword = -not [string]::IsNullOrWhiteSpace(
    [Environment]::GetEnvironmentVariable('TENANTPULSE_SHARED_PASSWORD', 'User'))

if ($mode -eq 'UsernamePassword' -and -not ($hasConfigPassword -or $hasUserEnvPassword)) {
    Write-Warning @"
Auth.Mode is UsernamePassword but no shared password is available to a scheduled task.
A process-scoped environment variable does not survive into one. Either:
    [Environment]::SetEnvironmentVariable('TENANTPULSE_SHARED_PASSWORD','<password>','User')
or set TenantPulse.Auth.SharedPassword in config/tenant-pulse.json (gitignored).
Without it the run works until a refresh token expires, then stops acting as that user.
"@
}

if ($config.TenantPulse.Simulation.DryRun -eq $true) {
    Write-Host '  Simulation.DryRun is true in config; the task passes --live, which overrides it.' -ForegroundColor DarkGray
}

# --- build so the task never pays for a first build --------------------------
Write-Host "Building $Configuration..." -ForegroundColor Cyan
dotnet build (Join-Path $RepoRoot 'src/TenantPulse.slnx') -c $Configuration -v minimal | Out-Null

$exe = Join-Path $RepoRoot "src/TenantPulse.Cli/bin/$Configuration/net10.0/tenant-pulse.exe"
if (-not (Test-Path $exe)) {
    throw "Build did not produce $exe"
}

# --- task definition ---------------------------------------------------------
# Prefer PowerShell 7 when present, but fall back to Windows PowerShell so this works on a stock
# machine. Repository scripts are kept ASCII-only precisely so 5.1 parses them correctly.
$psExe = (Get-Command pwsh.exe -ErrorAction SilentlyContinue)?.Source
if (-not $psExe) { $psExe = (Get-Command powershell.exe).Source }
Write-Host "  Host        $psExe" -ForegroundColor DarkGray

$arguments = '-NoProfile -NonInteractive -ExecutionPolicy Bypass -WindowStyle Hidden ' +
             "-File `"$launcher`" -RepoRoot `"$RepoRoot`" -Configuration $Configuration"

$action = New-ScheduledTaskAction -Execute $psExe -Argument $arguments -WorkingDirectory $RepoRoot

$triggers = @()
if ($RunWhetherLoggedOn) {
    $triggers += New-ScheduledTaskTrigger -AtStartup
}
else {
    $triggers += New-ScheduledTaskTrigger -AtLogOn -User $env:USERNAME
}

# Safety net: if the simulator ever died, start it again. IgnoreNew makes this a no-op when it is
# already running, so it can never double-post.
$triggers += New-ScheduledTaskTrigger -Daily -At $RepairAt

$settings = New-ScheduledTaskSettingsSet `
    -MultipleInstances IgnoreNew `
    -AllowStartIfOnBatteries `
    -DontStopIfGoingOnBatteries `
    -StartWhenAvailable `
    -RestartCount 3 `
    -RestartInterval (New-TimeSpan -Minutes 5) `
    -ExecutionTimeLimit (New-TimeSpan -Seconds 0)

if (Get-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue) {
    Write-Host "Replacing existing task '$TaskName'..." -ForegroundColor DarkGray
    Unregister-ScheduledTask -TaskName $TaskName -Confirm:$false
}

$description = 'Simulates realistic daily user activity in a CDX demo tenant (tenant-pulse run --live).'

if ($RunWhetherLoggedOn) {
    Write-Host 'Registering to run whether or not you are logged on.' -ForegroundColor Cyan
    Write-Host '  Windows needs your password to log you on in the background.' -ForegroundColor DarkGray

    $credential = Get-Credential -UserName "$env:USERDOMAIN\$env:USERNAME" `
        -Message 'Windows password for the tenant-pulse scheduled task'

    Register-ScheduledTask -TaskName $TaskName `
        -Action $action -Trigger $triggers -Settings $settings -Description $description `
        -User $credential.UserName `
        -Password $credential.GetNetworkCredential().Password `
        -RunLevel Limited | Out-Null
}
else {
    $principal = New-ScheduledTaskPrincipal -UserId "$env:USERDOMAIN\$env:USERNAME" `
        -LogonType Interactive -RunLevel Limited

    Register-ScheduledTask -TaskName $TaskName `
        -Action $action -Trigger $triggers -Settings $settings -Description $description `
        -Principal $principal | Out-Null
}

Write-Host ''
Write-Host ('-' * 60)
Write-Host "Registered '$TaskName'." -ForegroundColor Green
Write-Host ''
Write-Host "  Runs as     $env:USERDOMAIN\$env:USERNAME (needed to decrypt the token cache)"
Write-Host ("  Starts      " + $(if ($RunWhetherLoggedOn) { 'at boot, logged on or not' } else { 'when you log on' }))
Write-Host "  Repairs     daily at $RepairAt if it is not already running"
Write-Host "  Log         .state/run.log"
Write-Host ''
Write-Host 'Control it with:' -ForegroundColor White
Write-Host "  Start-ScheduledTask $TaskName"
Write-Host "  Stop-ScheduledTask $TaskName"
Write-Host "  New-Item .state\STOP          # kill switch: stops the simulator within a minute"
Write-Host "  ./scripts/install-daily-task.ps1 -Remove"
Write-Host ''
