<#
.SYNOPSIS
    Deploys tenant-pulse to Azure Container Apps, so the demo tenant stays alive without a PC.

.DESCRIPTION
    Creates (idempotently) everything the simulator needs and deploys the current source:

      * Azure Container Registry, and builds the image server-side - no local Docker required
      * a storage account and file share for durable state
      * a Container Apps environment with that share attached
      * the container app itself, pinned to exactly one replica

    Why one replica: the simulator is single-writer by design. Two of them would double-post into
    the tenant and fight over the journal.

    State is split deliberately. The SQLite journal runs on the container's local disk, because
    SQLite cannot run on an SMB file share - the byte-range locking it needs is unsupported and
    every statement fails with "database is locked". It is snapshotted onto the share instead, which
    is what keeps 'purge' able to clean the tenant up after a restart. The MSAL token caches are
    plain files, so they live on the share directly.

    Nothing secret is baked into the image. The Azure OpenAI key and the shared demo password are
    Container Apps secrets, injected as environment variables at run time.

.PARAMETER TenantId
    The demo tenant to act against. Also the only tenant the deployment will allow.

.PARAMETER ClientId
    Application (client) id of the public-client app registration in that tenant.

.PARAMETER Domain
    Primary domain of the demo tenant, e.g. m365cpi000000.onmicrosoft.com.

.PARAMETER DirectoryReader
    UPN used to read the directory on startup. Any enrolled demo user will do; it must not be an
    account excluded from the simulated workforce, and it must be able to sign in unattended.

.PARAMETER SharedPassword
    Shared demo-user password. Prompted for securely if omitted.

.PARAMETER SubscriptionId
    Azure subscription to deploy into.

.PARAMETER ResourceGroup
    Resource group name. Created if missing.

.PARAMETER Location
    Azure region.

.PARAMETER NamePrefix
    Prefix for generated resource names. Must be short and lowercase.

.PARAMETER OpenAiEndpoint
    Existing Azure OpenAI endpoint. Content generation falls back to templates without one.

.PARAMETER OpenAiDeployment
    Azure OpenAI deployment (model) name.

.PARAMETER CompanyName
    Fictional company the personas work for. Should match the tenant's own content.

.PARAMETER CompanyIndustry
    What that company does. Steers every generated mail, chat and document.

.EXAMPLE
    ./scripts/deploy-azure.ps1 -TenantId <guid> -ClientId <guid> -Domain contoso.onmicrosoft.com `
        -DirectoryReader user@contoso.onmicrosoft.com
#>

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)] [string] $TenantId,
    [Parameter(Mandatory = $true)] [string] $ClientId,
    [Parameter(Mandatory = $true)] [string] $Domain,
    [Parameter(Mandatory = $true)] [string] $DirectoryReader,

    [securestring] $SharedPassword,

    [string] $SubscriptionId,
    [string] $ResourceGroup = 'rg-tenant-pulse',
    [string] $Location = 'eastus',
    [string] $NamePrefix = 'tenantpulse',

    [string] $OpenAiEndpoint,
    [string] $OpenAiDeployment = 'gpt-4.1-mini',
    [string] $OpenAiKey,

    [string] $CompanyName = 'Contoso',
    [string] $CompanyIndustry = 'professional services',

    [string] $Tag = "v$(Get-Date -Format 'yyyyMMddHHmm')"
)

$ErrorActionPreference = 'Stop'

$repoRoot = (Resolve-Path (Split-Path -Parent $PSScriptRoot)).Path
Push-Location $repoRoot

try {
    if (-not (Get-Command az -ErrorAction SilentlyContinue)) {
        throw 'Azure CLI (az) not found.'
    }

    if (-not $SharedPassword) {
        $SharedPassword = Read-Host -AsSecureString `
            'Shared demo-user password (used to enrol users unattended)'
    }

    $plainPassword = [System.Net.NetworkCredential]::new('', $SharedPassword).Password
    if ([string]::IsNullOrWhiteSpace($plainPassword)) {
        throw 'A shared password is required: the container enrols users unattended.'
    }

    if ($SubscriptionId) {
        az account set --subscription $SubscriptionId --only-show-errors
    }
    $SubscriptionId = az account show --query id -o tsv --only-show-errors

    # Storage account names allow neither hyphens nor capitals, and cap at 24 characters.
    $suffix   = ($Domain -replace '[^0-9]', '')
    if ($suffix.Length -gt 8) { $suffix = $suffix.Substring(0, 8) }
    $acrName  = ("acr$NamePrefix$suffix" -replace '[^a-z0-9]', '').ToLowerInvariant()
    $stName   = ("st$NamePrefix$suffix"  -replace '[^a-z0-9]', '').ToLowerInvariant()
    if ($stName.Length  -gt 24) { $stName  = $stName.Substring(0, 24) }
    if ($acrName.Length -gt 50) { $acrName = $acrName.Substring(0, 50) }

    $envName   = "cae-$NamePrefix"
    $appName   = "ca-$NamePrefix"
    $shareName = 'tenant-pulse-state'
    $image     = "$acrName.azurecr.io/tenant-pulse:$Tag"

    Write-Host ''
    Write-Host 'tenant-pulse - Azure Container Apps deployment' -ForegroundColor White
    Write-Host ('-' * 62)
    Write-Host "  Subscription  $SubscriptionId"
    Write-Host "  Group         $ResourceGroup ($Location)"
    Write-Host "  Registry      $acrName"
    Write-Host "  Storage       $stName/$shareName"
    Write-Host "  App           $appName"
    Write-Host "  Image         $image"
    Write-Host ''

    Write-Host 'Registering resource providers...' -ForegroundColor Cyan
    foreach ($p in 'Microsoft.App', 'Microsoft.ContainerRegistry', 'Microsoft.OperationalInsights', 'Microsoft.Storage') {
        if ((az provider show -n $p --query registrationState -o tsv --only-show-errors 2>$null) -ne 'Registered') {
            Write-Host "  registering $p (this can take a few minutes)" -ForegroundColor DarkGray
            az provider register -n $p --wait --only-show-errors | Out-Null
        }
    }

    Write-Host 'Resource group...' -ForegroundColor Cyan
    az group create -n $ResourceGroup -l $Location --only-show-errors | Out-Null

    Write-Host 'Container registry...' -ForegroundColor Cyan
    az acr create -n $acrName -g $ResourceGroup -l $Location --sku Basic --admin-enabled true --only-show-errors | Out-Null

    Write-Host 'Building the image in Azure (no local Docker needed)...' -ForegroundColor Cyan
    az acr build -r $acrName -t "tenant-pulse:$Tag" -t 'tenant-pulse:latest' -f Dockerfile . | Out-Null

    Write-Host 'Storage for durable state...' -ForegroundColor Cyan
    az storage account create -n $stName -g $ResourceGroup -l $Location `
        --sku Standard_LRS --kind StorageV2 --min-tls-version TLS1_2 `
        --allow-blob-public-access false --only-show-errors | Out-Null
    az storage share-rm create --storage-account $stName -g $ResourceGroup -n $shareName --quota 5 --only-show-errors | Out-Null

    $storageKey = az storage account keys list -n $stName -g $ResourceGroup --query '[0].value' -o tsv --only-show-errors

    Write-Host 'Container Apps environment...' -ForegroundColor Cyan
    az containerapp env create -n $envName -g $ResourceGroup -l $Location --only-show-errors | Out-Null
    az containerapp env storage set -n $envName -g $ResourceGroup `
        --storage-name statestore `
        --azure-file-account-name $stName `
        --azure-file-account-key $storageKey `
        --azure-file-share-name $shareName `
        --access-mode ReadWrite --only-show-errors | Out-Null

    if (-not $OpenAiKey -and $OpenAiEndpoint) {
        $aoaiName = ([uri]$OpenAiEndpoint).Host.Split('.')[0]
        $OpenAiKey = az cognitiveservices account keys list -n $aoaiName -g $ResourceGroup --query key1 -o tsv --only-show-errors 2>$null
    }
    if (-not $OpenAiKey) { $OpenAiKey = '' }

    $acrKey = az acr credential show -n $acrName --query 'passwords[0].value' -o tsv --only-show-errors

    # The full spec goes through a file: the shared password routinely contains characters that a
    # command line would mangle, and a file keeps it out of the process list.
    $envId = az containerapp env show -n $envName -g $ResourceGroup --query id -o tsv --only-show-errors

    $contentEnv = if ($OpenAiEndpoint) {
@"
          - name: TENANTPULSE_TenantPulse__Content__Provider
            value: AzureOpenAI
          - name: TENANTPULSE_TenantPulse__Content__Endpoint
            value: $OpenAiEndpoint
          - name: TENANTPULSE_TenantPulse__Content__Deployment
            value: $OpenAiDeployment
          - name: TENANTPULSE_AOAI_KEY
            secretRef: aoai-key
"@
    } else {
@"
          - name: TENANTPULSE_TenantPulse__Content__Provider
            value: Template
"@
    }

    $yaml = @"
location: $Location
name: $appName
type: Microsoft.App/containerApps
properties:
  environmentId: $envId
  configuration:
    activeRevisionsMode: Single
    secrets:
      - name: aoai-key
        value: '$OpenAiKey'
      - name: shared-password
        value: '$plainPassword'
      - name: acr-password
        value: '$acrKey'
    registries:
      - server: $acrName.azurecr.io
        username: $acrName
        passwordSecretRef: acr-password
  template:
    containers:
      - name: tenant-pulse
        image: $image
        args:
          - run
          - --live
          - --as
          - $DirectoryReader
        resources:
          cpu: 0.5
          memory: 1.0Gi
        env:
          - name: TENANTPULSE_SHARED_PASSWORD
            secretRef: shared-password
          - name: TENANTPULSE_TenantPulse__Tenant__TenantId
            value: $TenantId
          - name: TENANTPULSE_TenantPulse__Tenant__AllowedTenantIds__0
            value: $TenantId
          - name: TENANTPULSE_TenantPulse__Tenant__AllowedDomains__0
            value: $Domain
          - name: TENANTPULSE_TenantPulse__Tenant__ClientId
            value: $ClientId
          - name: TENANTPULSE_TenantPulse__Auth__Mode
            value: UsernamePassword
          - name: TENANTPULSE_TenantPulse__Auth__CacheDirectory
            value: /app/.state/token-cache
          - name: TENANTPULSE_TenantPulse__Simulation__DryRun
            value: 'false'
          - name: TENANTPULSE_TenantPulse__Simulation__JournalPath
            value: /tmp/journal.db
          - name: TENANTPULSE_TenantPulse__Simulation__JournalSnapshotPath
            value: /app/.state/journal.db
          - name: TENANTPULSE_TenantPulse__Simulation__KillSwitchFile
            value: /app/.state/STOP
          - name: TENANTPULSE_TenantPulse__Content__CompanyName
            value: '$CompanyName'
          - name: TENANTPULSE_TenantPulse__Content__CompanyIndustry
            value: '$CompanyIndustry'
$contentEnv
        volumeMounts:
          - volumeName: state
            mountPath: /app/.state
    volumes:
      - name: state
        storageType: AzureFile
        storageName: statestore
    scale:
      minReplicas: 1
      maxReplicas: 1
"@

    $specPath = Join-Path ([System.IO.Path]::GetTempPath()) "tenant-pulse-$([guid]::NewGuid().ToString('N')).yaml"
    Set-Content -Path $specPath -Value $yaml -Encoding UTF8

    try {
        $exists = az containerapp show -n $appName -g $ResourceGroup --only-show-errors 2>$null
        if ($exists) {
            Write-Host 'Updating the container app...' -ForegroundColor Cyan
            az containerapp update -n $appName -g $ResourceGroup --yaml $specPath --only-show-errors | Out-Null
        }
        else {
            Write-Host 'Creating the container app...' -ForegroundColor Cyan
            az containerapp create -n $appName -g $ResourceGroup --yaml $specPath --only-show-errors | Out-Null
        }
    }
    finally {
        Remove-Item $specPath -Force -ErrorAction SilentlyContinue
    }

    Write-Host ''
    Write-Host ('-' * 62)
    Write-Host 'Deployed.' -ForegroundColor Green
    Write-Host ''
    Write-Host '  Follow it       ' -NoNewline; Write-Host "az containerapp logs show -n $appName -g $ResourceGroup --follow"
    Write-Host '  Stop it         ' -NoNewline; Write-Host "az containerapp update -n $appName -g $ResourceGroup --min-replicas 0 --max-replicas 0"
    Write-Host '  Kill switch     ' -NoNewline; Write-Host "upload a file named STOP to the $shareName share"
    Write-Host '  Clean the tenant' -NoNewline; Write-Host "  download journal.db from the share and run: tenant-pulse purge --live"
    Write-Host ''
}
finally {
    Pop-Location
}
