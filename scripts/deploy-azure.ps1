<#
.SYNOPSIS
    Deploys tenant-pulse to Azure Container Apps, so the demo tenant stays alive without a PC.

.DESCRIPTION
    Creates (idempotently) everything the simulator needs and deploys the current source:

      * Azure Container Registry, and builds the image server-side - no local Docker required
      * a storage account, file share and journal table for durable state
      * a virtual network, with private endpoints onto that storage account
      * a Container Apps environment inside that network, with the share attached
      * the container app itself, pinned to exactly one replica

    Why one replica: the simulator is single-writer by design. Two of them would double-post into
    the tenant and fight over the journal.

    The activity journal is an Azure Table, not a file. That is what makes a hosted run observable:
    'tenant-pulse report' and 'tenant-pulse purge' read the table directly, so what the simulator
    has done - per persona, per activity, including failures and links to the mail, documents,
    meetings and messages it created - can be reviewed from anywhere, rather than only from
    wherever the database file happened to sit. It also retires the SQLite-on-SMB snapshot dance:
    SQLite cannot run on a file share, so the journal used to live on disposable container disk and
    be copied across. The share now only carries the MSAL token caches, which are plain files.

    The storage account holds refresh tokens for every demo user, so its public endpoint is
    disabled and it is reached over private endpoints. That is why the environment is VNet
    integrated - a private endpoint is only resolvable from inside the network. It is also load
    bearing rather than decorative: with public access disabled and no VNet, the file share fails
    to mount and the container quietly runs with no durable state at all.

    Nothing secret is baked into the image. The Azure OpenAI key and the shared demo password are
    Container Apps secrets, injected as environment variables at run time, and the journal is
    written with the app's own managed identity rather than a storage account key.

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

    [string] $VNetAddressPrefix = '10.40.0.0/16',
    [string] $InfrastructureSubnetPrefix = '10.40.0.0/23',
    [string] $PrivateEndpointSubnetPrefix = '10.40.2.0/24',

    [switch] $PrivateNetworking,
    [switch] $RecreateEnvironment,

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
    #
    # The trailing digits, not the leading ones: a CDX domain looks like m365cpiNNNNNNNN, and the
    # part that identifies the tenant is the tail. Taking the front would yield the "365" of the
    # product name plus a truncated tenant number - both meaningless and, for an existing
    # deployment, a different name from every resource already there.
    $suffix   = ($Domain -replace '[^0-9]', '')
    if ($suffix.Length -gt 8) { $suffix = $suffix.Substring($suffix.Length - 8) }
    $acrName  = ("acr$NamePrefix$suffix" -replace '[^a-z0-9]', '').ToLowerInvariant()
    $stName   = ("st$NamePrefix$suffix"  -replace '[^a-z0-9]', '').ToLowerInvariant()
    if ($stName.Length  -gt 24) { $stName  = $stName.Substring(0, 24) }
    if ($acrName.Length -gt 50) { $acrName = $acrName.Substring(0, 50) }

    $envName   = "cae-$NamePrefix"
    $appName   = "ca-$NamePrefix"
    $vnetName  = "vnet-$NamePrefix"
    $shareName = 'tenant-pulse-state'
    $tableName = 'TenantPulseJournal'
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

    $storageId = az storage account show -n $stName -g $ResourceGroup --query id -o tsv --only-show-errors
    $infraSubnetId = $null

    # ------------------------------------------------------------------------------------------
    # Network. Opt-in, via -PrivateNetworking.
    #
    # Reaching storage over private endpoints requires the Container Apps environment to sit in the
    # VNet, because a private endpoint is only resolvable and routable from inside one. That in turn
    # requires the subnet to have its own outbound path to the internet - the simulator talks to
    # Microsoft Graph, and the platform has to pull the image - which normally means a NAT gateway.
    #
    # Both halves have to be available or neither works, and in a locked-down subscription they
    # frequently are not: a policy that blocks public IP addresses blocks NAT gateways with them,
    # and the environment then cannot pull so much as mcr.microsoft.com. The symptom is silent -
    # "Deployment Progress Deadline Exceeded. 0/1 replicas ready" with no pull or mount error - so
    # check egress first if a VNet environment will not start.
    # ------------------------------------------------------------------------------------------
    if ($PrivateNetworking) {
        Write-Host 'Virtual network...' -ForegroundColor Cyan
        az network vnet create -n $vnetName -g $ResourceGroup -l $Location `
            --address-prefixes $VNetAddressPrefix --only-show-errors | Out-Null

        # Container Apps requires its infrastructure subnet delegated to Microsoft.App/environments,
        # and will not accept anything smaller than a /23 for a Consumption environment.
        az network vnet subnet create -n 'snet-infra' -g $ResourceGroup --vnet-name $vnetName `
            --address-prefixes $InfrastructureSubnetPrefix `
            --delegations 'Microsoft.App/environments' --only-show-errors | Out-Null

        az network vnet subnet create -n 'snet-pe' -g $ResourceGroup --vnet-name $vnetName `
            --address-prefixes $PrivateEndpointSubnetPrefix --only-show-errors | Out-Null

        $infraSubnetId = az network vnet subnet show -n 'snet-infra' -g $ResourceGroup --vnet-name $vnetName `
            --query id -o tsv --only-show-errors

        # Outbound egress for the subnet. Without it nothing in the environment starts.
        Write-Host 'NAT gateway for outbound access...' -ForegroundColor Cyan
        az network public-ip create -n "pip-$NamePrefix-nat" -g $ResourceGroup -l $Location `
            --sku Standard --allocation-method Static --only-show-errors | Out-Null

        if ($LASTEXITCODE -ne 0) {
            throw "Could not create a public IP for the NAT gateway. Without outbound access a " +
                  "VNet environment cannot pull its image or reach Microsoft Graph, so this " +
                  "deployment would never start. Re-run without -PrivateNetworking, or get the " +
                  "subscription registered for Microsoft.Network/AllowBringYourOwnPublicIpAddress."
        }

        az network nat gateway create -n "natgw-$NamePrefix" -g $ResourceGroup -l $Location `
            --public-ip-addresses "pip-$NamePrefix-nat" --idle-timeout 10 --only-show-errors | Out-Null
        az network vnet subnet update -n 'snet-infra' -g $ResourceGroup --vnet-name $vnetName `
            --nat-gateway "natgw-$NamePrefix" --only-show-errors | Out-Null

        # One endpoint per sub-resource: 'file' carries the token cache mount, 'table' the journal.
        foreach ($group in 'file', 'table') {
            Write-Host "  private endpoint for $group" -ForegroundColor DarkGray
            $zone = "privatelink.$group.core.windows.net"

            az network private-dns zone create -g $ResourceGroup -n $zone --only-show-errors | Out-Null
            az network private-dns link vnet create -g $ResourceGroup -n "link-$group" `
                --zone-name $zone --virtual-network $vnetName --registration-enabled false `
                --only-show-errors | Out-Null

            az network private-endpoint create -n "pe-$NamePrefix-$group" -g $ResourceGroup -l $Location `
                --vnet-name $vnetName --subnet 'snet-pe' `
                --private-connection-resource-id $storageId --group-id $group `
                --connection-name "conn-$group" --only-show-errors | Out-Null

            # Without the zone group the endpoint exists but nothing resolves to it, and every call
            # keeps going to the blocked public endpoint.
            az network private-endpoint dns-zone-group create -g $ResourceGroup `
                --endpoint-name "pe-$NamePrefix-$group" -n 'default' `
                --private-dns-zone $zone --zone-name $group --only-show-errors | Out-Null
        }
    }

    Write-Host 'Container Apps environment...' -ForegroundColor Cyan

    # VNet membership is immutable on an environment, so switching -PrivateNetworking on or off
    # means replacing it rather than updating it.
    $existingEnv = az containerapp env show -n $envName -g $ResourceGroup --only-show-errors 2>$null
    if ($existingEnv) {
        $currentSubnet = az containerapp env show -n $envName -g $ResourceGroup `
            --query 'properties.vnetConfiguration.infrastructureSubnetId' -o tsv --only-show-errors

        $wantSubnet = if ($PrivateNetworking) { $infraSubnetId } else { '' }
        if (-not $currentSubnet) { $currentSubnet = '' }

        if ($currentSubnet -ne $wantSubnet) {
            if (-not $RecreateEnvironment) {
                throw "Environment $envName has different network settings and that cannot be " +
                      "changed in place. Re-run with -RecreateEnvironment to delete and rebuild it."
            }

            Write-Host "  deleting $envName so it can be rebuilt" -ForegroundColor Yellow
            az containerapp delete -n $appName -g $ResourceGroup --yes --only-show-errors 2>$null | Out-Null
            az containerapp env delete -n $envName -g $ResourceGroup --yes --only-show-errors | Out-Null

            # 'env delete' returns as soon as the delete is accepted, not when it is done, and
            # creating over a half-deleted environment fails with ManagedEnvironmentScheduledForDelete.
            Write-Host '  waiting for the deletion to finish' -ForegroundColor DarkGray
            $deadline = (Get-Date).AddMinutes(20)
            while ((Get-Date) -lt $deadline) {
                $still = az containerapp env show -n $envName -g $ResourceGroup --only-show-errors 2>$null
                if (-not $still) { break }
                Start-Sleep -Seconds 15
            }

            if (az containerapp env show -n $envName -g $ResourceGroup --only-show-errors 2>$null) {
                throw "Environment $envName is still deleting after 20 minutes. Re-run once it has gone."
            }
        }
    }

    if ($PrivateNetworking) {
        az containerapp env create -n $envName -g $ResourceGroup -l $Location `
            --infrastructure-subnet-resource-id $infraSubnetId --only-show-errors | Out-Null
    }
    else {
        az containerapp env create -n $envName -g $ResourceGroup -l $Location --only-show-errors | Out-Null
    }

    # az reports failures on stderr and a non-zero exit code, neither of which stops a PowerShell
    # script by default. Without this check the rest of the deployment runs against nothing and
    # reports success at the end.
    if (-not (az containerapp env show -n $envName -g $ResourceGroup --only-show-errors 2>$null)) {
        throw "Environment $envName was not created. Re-run; the error above says why."
    }

    if ($PrivateNetworking) {
        az containerapp env storage set -n $envName -g $ResourceGroup `
            --storage-name statestore `
            --azure-file-account-name $stName `
            --azure-file-account-key $storageKey `
            --azure-file-share-name $shareName `
            --access-mode ReadWrite --only-show-errors | Out-Null

        # Now that everything reaches the account privately, close the public door. Done after the
        # share is wired up, because that call itself goes over the public endpoint.
        Write-Host 'Closing public network access to storage...' -ForegroundColor Cyan
        az storage account update -n $stName -g $ResourceGroup `
            --public-network-access Disabled --only-show-errors | Out-Null
    }

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

    # Where the journal and the token cache go depends on whether storage is reachable at all.
    #
    # With private networking the app writes the journal table with its managed identity and mounts
    # the file share for the token cache. Without it the account's public endpoint is usually closed
    # by policy, so the share cannot be mounted - and a volume pointing at an unreachable share does
    # not degrade gracefully, it puts the container into CrashLoopBackOff with no log output at all.
    # So in that mode nothing is mounted and state is container-local and disposable: the journal is
    # SQLite on /tmp, and the users are re-enrolled by ROPC after every restart.
    if ($PrivateNetworking) {
        $statePath  = '/app/.state'
        $journalEnv = @"
          - name: TENANTPULSE_TenantPulse__Simulation__JournalTable__Endpoint
            value: https://$stName.table.core.windows.net
          - name: TENANTPULSE_TenantPulse__Simulation__JournalTable__TableName
            value: $tableName
"@
        $volumeYaml = @"
        volumeMounts:
          - volumeName: state
            mountPath: /app/.state
    volumes:
      - name: state
        storageType: AzureFile
        storageName: statestore
"@
    }
    else {
        $statePath  = '/tmp'
        $journalEnv = @"
          - name: TENANTPULSE_TenantPulse__Simulation__DefaultTimeZone
            value: Europe/London
"@
        $volumeYaml = ''
    }

    $yaml = @"
location: $Location
name: $appName
type: Microsoft.App/containerApps
identity:
  type: SystemAssigned
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
            value: $statePath/token-cache
          - name: TENANTPULSE_TenantPulse__Simulation__DryRun
            value: 'false'
          - name: TENANTPULSE_TenantPulse__Simulation__JournalPath
            value: /tmp/journal.db
$journalEnv
          - name: TENANTPULSE_TenantPulse__Simulation__KillSwitchFile
            value: $statePath/STOP
          - name: TENANTPULSE_TenantPulse__Content__CompanyName
            value: '$CompanyName'
          - name: TENANTPULSE_TenantPulse__Content__CompanyIndustry
            value: '$CompanyIndustry'
$contentEnv
$volumeYaml
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
            $result = az containerapp update -n $appName -g $ResourceGroup --yaml $specPath --only-show-errors 2>&1
        }
        else {
            Write-Host 'Creating the container app...' -ForegroundColor Cyan
            $result = az containerapp create -n $appName -g $ResourceGroup --yaml $specPath --only-show-errors 2>&1
        }

        # Keep the failure, not just the fact of it. A rejected spec is the most likely thing to go
        # wrong here and the message names the offending field.
        if ($LASTEXITCODE -ne 0) {
            Write-Host ''
            $result | ForEach-Object { Write-Host "  $_" -ForegroundColor Red }
            Write-Host ''
            Write-Host "  Spec kept for inspection: $specPath" -ForegroundColor Yellow
            throw "Deploying $appName failed."
        }
    }
    finally {
        if ($LASTEXITCODE -eq 0) {
            Remove-Item $specPath -Force -ErrorAction SilentlyContinue
        }
    }

    if (-not (az containerapp show -n $appName -g $ResourceGroup --only-show-errors 2>$null)) {
        throw "Container app $appName was not created. The error above says why."
    }

    # The journal is written with the app's own identity rather than an account key, because a
    # StorageAccount_DisableLocalAuth policy will happily switch shared-key access off underneath
    # a deployment that depends on it. Only needed when the journal actually lives in the table.
    if ($PrivateNetworking) {
        Write-Host 'Granting the app access to the journal table...' -ForegroundColor Cyan
        $principalId = az containerapp show -n $appName -g $ResourceGroup `
            --query identity.principalId -o tsv --only-show-errors

        if ($principalId) {
            az role assignment create --assignee-object-id $principalId `
                --assignee-principal-type ServicePrincipal `
                --role 'Storage Table Data Contributor' `
                --scope $storageId --only-show-errors 2>$null | Out-Null
        }
        else {
            Write-Host '  no managed identity was returned - grant the role by hand.' -ForegroundColor Yellow
        }
    }

    Write-Host ''
    Write-Host ('-' * 62)
    Write-Host 'Deployed.' -ForegroundColor Green
    Write-Host ''
    Write-Host '  Follow it       ' -NoNewline; Write-Host "az containerapp logs show -n $appName -g $ResourceGroup --follow"
    Write-Host '  Stop it         ' -NoNewline; Write-Host "az containerapp update -n $appName -g $ResourceGroup --min-replicas 0 --max-replicas 0"
    Write-Host ''

    if ($PrivateNetworking) {
        Write-Host '  Kill switch     ' -NoNewline; Write-Host "upload a file named STOP to the $shareName share"
        Write-Host '  What it has done' -NoNewline; Write-Host "  tenant-pulse report --since 7 --recent 20"
        Write-Host '  Clean the tenant' -NoNewline; Write-Host "  tenant-pulse purge --since 30 --live"
        Write-Host ''
        Write-Host '  report and purge read the journal table directly, so they work from here. They'
        Write-Host '  need line of sight to the private endpoint (VPN, jumpbox, or a peered network)'
        Write-Host '  and the Storage Table Data Reader role. Point them at it with:'
        Write-Host ''
        Write-Host "    `$env:TENANTPULSE_TenantPulse__Simulation__JournalTable__Endpoint = 'https://$stName.table.core.windows.net'"
    }
    else {
        Write-Host '  Kill switch     ' -NoNewline; Write-Host "scale to zero (above). There is no mounted share to drop a STOP file on."
        Write-Host ''
        Write-Host '  NOTE: deployed without -PrivateNetworking, so nothing is mounted and the'
        Write-Host '  journal is SQLite on container-local disk. It does NOT survive a restart,'
        Write-Host '  which means purge cannot clean up afterwards and report has nothing to read'
        Write-Host '  remotely. The console log is the only record - query it with:'
        Write-Host ''
        Write-Host "    az containerapp logs show -n $appName -g $ResourceGroup --tail 100"
        Write-Host ''
        Write-Host '  For a durable, queryable journal, re-run with -PrivateNetworking. That needs'
        Write-Host '  the subscription to allow a public IP for the NAT gateway, because a VNet'
        Write-Host '  environment with no outbound path cannot pull its image or reach Graph.'
    }

    Write-Host ''
}
finally {
    Pop-Location
}
