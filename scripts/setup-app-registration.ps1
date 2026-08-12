<#
.SYNOPSIS
    Creates the Entra public-client app registration tenant-pulse needs, and writes a starter config.

.DESCRIPTION
    tenant-pulse acts as each demo user, which needs a public-client app registration with delegated
    Microsoft Graph permissions and admin consent. Doing that by hand in the portal is the fiddliest
    part of setup, so this does it for you.

    Idempotent: re-running finds the existing app rather than creating a duplicate.

    Requires the Azure CLI, signed in as an administrator of the DEMO tenant:
        az login --allow-no-subscriptions --tenant <tenant-id>

.PARAMETER TenantId
    The CDX demo tenant to target. Also written to the config as the one allowed tenant.

.PARAMETER DisplayName
    App registration display name.

.PARAMETER ConfigPath
    Where to write the starter config. Skipped if the file already exists (never overwrites).

.PARAMETER IncludeCopilotExport
    Also request the AiEnterpriseInteraction.Read.All APPLICATION permission, which 'verify-copilot'
    needs to read Copilot interaction history back.

.EXAMPLE
    ./scripts/setup-app-registration.ps1 -TenantId 00000000-0000-0000-0000-000000000000
#>

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $TenantId,

    [string] $DisplayName = 'tenant-pulse',

    [string] $ConfigPath = 'config/tenant-pulse.json',

    [switch] $IncludeCopilotExport
)

$ErrorActionPreference = 'Stop'

$GraphAppId = '00000003-0000-0000-c000-000000000000'

# Delegated scopes tenant-pulse uses. Keep in sync with AuthOptions.Scopes.
$DelegatedScopes = @(
    'User.Read'
    'User.ReadBasic.All'
    'Mail.ReadWrite'
    'Mail.Send'
    'Chat.ReadWrite'
    'ChannelMessage.Send'
    'ChannelMessage.Read.All'
    'Team.ReadBasic.All'
    'Channel.ReadBasic.All'
    'Files.ReadWrite.All'
    'Sites.ReadWrite.All'
    'Calendars.ReadWrite'
)

function Assert-AzureCli {
    if (-not (Get-Command az -ErrorAction SilentlyContinue)) {
        throw 'Azure CLI (az) not found. Install it, then: az login --allow-no-subscriptions --tenant <tenant-id>'
    }

    $account = az account show --only-show-errors 2>$null | ConvertFrom-Json
    if (-not $account) {
        throw "Not signed in. Run: az login --allow-no-subscriptions --tenant $TenantId"
    }

    if ($account.tenantId -ne $TenantId) {
        throw ("Signed into tenant $($account.tenantId) but -TenantId is $TenantId. " +
               "Run: az login --allow-no-subscriptions --tenant $TenantId")
    }

    Write-Host "  Signed in to $($account.tenantId) as $($account.user.name)" -ForegroundColor DarkGray
}

function Get-GraphPermissionMap {
    Write-Host 'Resolving Microsoft Graph permission ids...' -ForegroundColor Cyan

    $graphSp = az ad sp show --id $GraphAppId --only-show-errors | ConvertFrom-Json

    $delegated = @{}
    foreach ($scope in $graphSp.oauth2PermissionScopes) {
        $delegated[$scope.value] = $scope.id
    }

    $application = @{}
    foreach ($role in $graphSp.appRoles) {
        $application[$role.value] = $role.id
    }

    return @{ Delegated = $delegated; Application = $application }
}

function Get-OrCreateApp {
    param([string] $Name)

    $existing = az ad app list --display-name $Name --only-show-errors | ConvertFrom-Json |
        Where-Object { $_.displayName -eq $Name } | Select-Object -First 1

    if ($existing) {
        Write-Host "  Found existing app '$Name' ($($existing.appId))" -ForegroundColor DarkGray
        return $existing
    }

    Write-Host "  Creating app registration '$Name'..." -ForegroundColor Cyan

    # isFallbackPublicClient=true is what enables device code and username/password flows.
    $created = az ad app create `
        --display-name $Name `
        --sign-in-audience AzureADMyOrg `
        --is-fallback-public-client true `
        --public-client-redirect-uris 'http://localhost' 'https://login.microsoftonline.com/common/oauth2/nativeclient' `
        --only-show-errors | ConvertFrom-Json

    return $created
}

Write-Host ''
Write-Host 'tenant-pulse - app registration setup' -ForegroundColor White
Write-Host ('-' * 60)

Assert-AzureCli

$graph = Get-GraphPermissionMap
$app = Get-OrCreateApp -Name $DisplayName
$appId = $app.appId

# --- permissions -----------------------------------------------------------
Write-Host 'Adding delegated Graph permissions...' -ForegroundColor Cyan

$missing = @()
$permissionArgs = @()
foreach ($scope in $DelegatedScopes) {
    if ($graph.Delegated.ContainsKey($scope)) {
        $permissionArgs += "$($graph.Delegated[$scope])=Scope"
    }
    else {
        $missing += $scope
    }
}

if ($missing.Count -gt 0) {
    Write-Warning "These delegated scopes were not found on Microsoft Graph and were skipped: $($missing -join ', ')"
}

if ($IncludeCopilotExport) {
    $exportRole = 'AiEnterpriseInteraction.Read.All'
    if ($graph.Application.ContainsKey($exportRole)) {
        $permissionArgs += "$($graph.Application[$exportRole])=Role"
        Write-Host "  Including application permission $exportRole (for verify-copilot)" -ForegroundColor DarkGray
    }
    else {
        Write-Warning "$exportRole not found on Microsoft Graph in this tenant; verify-copilot will not be able to read interaction history."
    }
}

az ad app permission add --id $appId --api $GraphAppId --api-permissions $permissionArgs --only-show-errors 2>$null | Out-Null

# --- service principal + consent -------------------------------------------
Write-Host 'Ensuring service principal exists...' -ForegroundColor Cyan
$sp = az ad sp list --filter "appId eq '$appId'" --only-show-errors | ConvertFrom-Json | Select-Object -First 1
if (-not $sp) {
    az ad sp create --id $appId --only-show-errors | Out-Null
    Start-Sleep -Seconds 5
}

Write-Host 'Granting admin consent...' -ForegroundColor Cyan
Write-Host '  (without this every user would be prompted individually at enrolment)' -ForegroundColor DarkGray

$consentOk = $true
try {
    az ad app permission admin-consent --id $appId --only-show-errors 2>$null | Out-Null
}
catch {
    $consentOk = $false
}

if (-not $consentOk) {
    Write-Warning @"
Admin consent could not be granted automatically (this often needs a moment after the app is created,
or a Global Administrator). Retry with:
    az ad app permission admin-consent --id $appId
or grant it in the portal: Entra ID > App registrations > $DisplayName > API permissions.
"@
}

# --- starter config --------------------------------------------------------
if (Test-Path $ConfigPath) {
    Write-Host ''
    Write-Host "Config already exists at $ConfigPath - not overwriting." -ForegroundColor Yellow
    Write-Host "  Set Tenant.ClientId to: $appId" -ForegroundColor Yellow
}
else {
    $examplePath = 'config/tenant-pulse.example.json'
    if (-not (Test-Path $examplePath)) {
        Write-Warning "Example config not found at $examplePath; skipping config generation."
    }
    else {
        $configDir = Split-Path -Parent $ConfigPath
        if ($configDir -and -not (Test-Path $configDir)) {
            New-Item -ItemType Directory -Path $configDir -Force | Out-Null
        }

        $defaultDomain = $null
        try {
            $domains = az rest --method GET `
                --url 'https://graph.microsoft.com/v1.0/domains?$select=id,isDefault' `
                --only-show-errors 2>$null | ConvertFrom-Json
            $defaultDomain = $domains.value | Where-Object { $_.isDefault } |
                Select-Object -First 1 -ExpandProperty id
        }
        catch {
            Write-Host '  (could not read the default domain; leaving the placeholder)' -ForegroundColor DarkGray
        }

        $config = (Get-Content $examplePath -Raw) -replace '00000000-0000-0000-0000-000000000000', $TenantId

        # ClientId shares the placeholder GUID with the tenant ids, so set it explicitly afterwards.
        $config = $config -replace '("ClientId":\s*)"[^"]*"', "`$1`"$appId`""

        if ($defaultDomain) {
            $config = $config -replace 'M365x000000\.onmicrosoft\.com', $defaultDomain
        }

        Set-Content -Path $ConfigPath -Value $config -Encoding UTF8
        Write-Host ''
        Write-Host "Wrote starter config to $ConfigPath" -ForegroundColor Green
        if ($defaultDomain) {
            Write-Host "  Default domain detected: $defaultDomain" -ForegroundColor DarkGray
        }
    }
}

Write-Host ''
Write-Host ('-' * 60)
Write-Host 'Done.' -ForegroundColor Green
Write-Host ''
Write-Host "  Tenant id   $TenantId"
Write-Host "  Client id   $appId"
Write-Host ''
Write-Host 'Next:' -ForegroundColor White
Write-Host '  1. dotnet run --project src/TenantPulse.Cli -- doctor'
Write-Host '  2. dotnet run --project src/TenantPulse.Cli -- bootstrap --user <a-demo-user-upn>'
Write-Host '  3. dotnet run --project src/TenantPulse.Cli -- bootstrap --all --as <that-upn>'
Write-Host '  4. dotnet run --project src/TenantPulse.Cli -- plan'
Write-Host '  5. dotnet run --project src/TenantPulse.Cli -- once --count 3 --live'
Write-Host ''
