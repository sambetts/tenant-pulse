<#
.SYNOPSIS
    Deploys the tenant-pulse activity workbook to Azure Monitor.

.DESCRIPTION
    Creates or updates an Azure Workbook that reports on what the simulator has done, reading the
    activity events the container writes to stdout and Log Analytics collects.

    This exists because the durable Azure Table journal sits behind a private endpoint. In a
    governed subscription the storage account keeps publicNetworkAccess disabled, so 'report' only
    works from inside the VNet. Log Analytics is reachable from any browser with RBAC, so the
    reporting path pushes out rather than being reached in for.

    Re-running updates the same workbook: the resource name is derived deterministically from the
    container app name, so a second run does not leave a duplicate behind.

.EXAMPLE
    .\deploy-report-workbook.ps1

.EXAMPLE
    .\deploy-report-workbook.ps1 -ResourceGroup rg-tenant-pulse -AppName ca-tenant-pulse
#>
[CmdletBinding()]
param(
    [string]$ResourceGroup = 'rg-tenant-pulse',
    [string]$AppName       = 'ca-tenant-pulse',
    [string]$DisplayName   = 'tenant-pulse activity',
    [string]$Location
)

$ErrorActionPreference = 'Stop'

function Assert-LastExitCode {
    param([string]$What)
    # az failures do not stop a PowerShell script on their own, and a deployment that carries on
    # against nothing will still report success.
    if ($LASTEXITCODE -ne 0) { throw "$What failed (exit $LASTEXITCODE)." }
}

Write-Host ''
Write-Host 'tenant-pulse - activity workbook' -ForegroundColor White
Write-Host ('-' * 62)

$contentPath = Join-Path $PSScriptRoot '..\azure\report-workbook.json'
if (-not (Test-Path $contentPath)) {
    throw "Workbook definition not found at $contentPath."
}

Write-Host 'Resolving the Log Analytics workspace...' -ForegroundColor Cyan

$envId = az containerapp show -n $AppName -g $ResourceGroup `
    --query properties.environmentId -o tsv --only-show-errors
Assert-LastExitCode 'Reading the container app'
if (-not $envId) { throw "Container app $AppName was not found in $ResourceGroup." }

$envName = $envId.Split('/')[-1]

$customerId = az containerapp env show -n $envName -g $ResourceGroup `
    --query properties.appLogsConfiguration.logAnalyticsConfiguration.customerId `
    -o tsv --only-show-errors
Assert-LastExitCode 'Reading the container app environment'
if (-not $customerId) {
    throw "Environment $envName has no Log Analytics workspace, so there is nothing to report on."
}

# The environment only records the workspace GUID, not its resource id, so find it by GUID.
$workspaceId = az monitor log-analytics workspace list `
    --query "[?customerId=='$customerId'].id | [0]" -o tsv --only-show-errors
Assert-LastExitCode 'Listing Log Analytics workspaces'
if (-not $workspaceId) {
    throw "No workspace with customer id $customerId is visible to this account."
}

Write-Host "  workspace  $workspaceId"

if (-not $Location) {
    $Location = az group show -n $ResourceGroup --query location -o tsv --only-show-errors
    Assert-LastExitCode 'Reading the resource group'
}

# A workbook resource must be named with a GUID. Deriving it from the app name keeps re-runs
# idempotent instead of scattering a new workbook on every deployment.
$md5   = [System.Security.Cryptography.MD5]::Create()
$hash  = $md5.ComputeHash([System.Text.Encoding]::UTF8.GetBytes("tenant-pulse-workbook:$ResourceGroup/$AppName"))
$md5.Dispose()
$workbookName = [guid]::new($hash).ToString()

$content = (Get-Content -Raw -Path $contentPath).
    Replace('__APP_NAME__', $AppName).
    Replace('__WORKSPACE_ID__', $workspaceId)

# Fail loudly here rather than letting Azure accept a workbook that renders as an error.
try { $null = $content | ConvertFrom-Json } catch { throw "report-workbook.json is not valid JSON: $_" }

$properties = [ordered]@{
    workbookName        = @{ value = $workbookName }
    workbookDisplayName = @{ value = $DisplayName }
    # The portal lists a workbook under its source resource by lowercase id; a cased id still
    # deploys but the workbook goes missing from the workspace gallery.
    workbookSourceId    = @{ value = $workspaceId.ToLowerInvariant() }
    workbookContent     = @{ value = $content }
    location            = @{ value = $Location }
}

$parameterFile = [ordered]@{
    '$schema'      = 'https://schema.management.azure.com/schemas/2019-04-01/deploymentParameters.json#'
    contentVersion = '1.0.0.0'
    parameters     = $properties
}

# The workbook definition is far too large for a command line, so parameters go through a file.
$tempFile = Join-Path ([System.IO.Path]::GetTempPath()) "tp-workbook-$([guid]::NewGuid()).json"
$parameterFile | ConvertTo-Json -Depth 10 | Set-Content -Path $tempFile -Encoding utf8

$templatePath = Join-Path $PSScriptRoot '..\azure\report-workbook.deploy.json'
if (-not (Test-Path $templatePath)) {
    throw "Deployment template not found at $templatePath."
}

try {
    Write-Host 'Deploying the workbook...' -ForegroundColor Cyan

    az deployment group create -g $ResourceGroup `
        --name "tenant-pulse-workbook-$(Get-Date -Format yyyyMMddHHmmss)" `
        --template-file $templatePath `
        --parameters "@$tempFile" `
        --only-show-errors | Out-Null
    Assert-LastExitCode 'Deploying the workbook'
}
finally {
    Remove-Item $tempFile -ErrorAction SilentlyContinue
}

$subscription = az account show --query id -o tsv --only-show-errors
$workbookId = "/subscriptions/$subscription/resourceGroups/$ResourceGroup/providers/Microsoft.Insights/workbooks/$workbookName"

Write-Host ''
Write-Host 'Workbook deployed.' -ForegroundColor Green
Write-Host "  https://portal.azure.com/#@/resource$workbookId"
Write-Host ''
Write-Host '  Pin it to a dashboard from the portal for one-click access.'
Write-Host ''
