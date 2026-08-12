<#
.SYNOPSIS
    Runs the tenant-pulse simulator, logging to a rolling file. Intended for a scheduler.

.DESCRIPTION
    A thin wrapper around 'tenant-pulse run --live' that a Windows scheduled task can call. It
    exists so the simulator can be started unattended and still leave a readable trail.

    It deliberately does NOT delete the kill switch: if .state/STOP is present the engine stops on
    purpose, and a scheduler must never override that.

    Refresh tokens in .state/token-cache are what keep this running day to day. The shared password
    is only the self-healing fallback for when a cache entry expires or is revoked - supply it via
    the TENANTPULSE_SHARED_PASSWORD environment variable if you want unattended re-enrolment.

.PARAMETER RepoRoot
    Repository root. Defaults to the parent of the folder holding this script.

.PARAMETER Configuration
    Build configuration to run. Defaults to Release.

.PARAMETER MaxLogBytes
    Roll the log once it exceeds this size. Defaults to 8 MB.

.EXAMPLE
    ./scripts/run-daily.ps1
#>

[CmdletBinding()]
param(
    [string] $RepoRoot,
    [ValidateSet('Debug', 'Release')]
    [string] $Configuration = 'Release',
    [int] $MaxLogBytes = 8MB
)

$ErrorActionPreference = 'Stop'

if (-not $RepoRoot) {
    $RepoRoot = Split-Path -Parent $PSScriptRoot
}

Set-Location $RepoRoot

$stateDir = Join-Path $RepoRoot '.state'
New-Item -ItemType Directory -Path $stateDir -Force | Out-Null

$logPath = Join-Path $stateDir 'run.log'

# Roll the log so an unattended run cannot fill the disk over weeks.
if ((Test-Path $logPath) -and ((Get-Item $logPath).Length -gt $MaxLogBytes)) {
    Move-Item $logPath (Join-Path $stateDir 'run.previous.log') -Force
}

$exe = Join-Path $RepoRoot "src/TenantPulse.Cli/bin/$Configuration/net10.0/tenant-pulse.exe"

if (-not (Test-Path $exe)) {
    "$(Get-Date -Format s) launcher: $exe not found - building $Configuration..." | Tee-Object -FilePath $logPath -Append
    dotnet build (Join-Path $RepoRoot 'src/TenantPulse.slnx') -c $Configuration -v minimal 2>&1 |
        Tee-Object -FilePath $logPath -Append
}

if (-not (Test-Path $exe)) {
    "$(Get-Date -Format s) launcher: build did not produce $exe - giving up." | Tee-Object -FilePath $logPath -Append
    exit 1
}

if (Test-Path (Join-Path $stateDir 'STOP')) {
    "$(Get-Date -Format s) launcher: kill switch present (.state/STOP) - not starting." |
        Tee-Object -FilePath $logPath -Append
    exit 0
}

"$(Get-Date -Format s) launcher: starting tenant-pulse run --live" | Tee-Object -FilePath $logPath -Append

& $exe run --live 2>&1 | Tee-Object -FilePath $logPath -Append

$code = $LASTEXITCODE
"$(Get-Date -Format s) launcher: tenant-pulse exited with $code" | Tee-Object -FilePath $logPath -Append
exit $code
