# Azure deployment

tenant-pulse runs as one continuously active Azure Container Apps replica. The deployment is
deliberately small, but its network and state design matters because the process acts as users in a
live CDX tenant and every created resource must remain purgeable.

## Resource map

| Resource | Name pattern | Why it exists |
|----------|--------------|---------------|
| Container App | `ca-<prefix>` | Runs `tenant-pulse run --live` as exactly one replica. |
| Container Apps environment | `cae-<prefix>` | Hosts the replica and connects it to the VNet. |
| Container Registry | `acr<prefix><suffix>` | Builds and stores versioned application images. |
| Storage account | `st<prefix><suffix>` | Hosts the durable Table journal and optional token-cache share. |
| Table | `TenantPulseJournal` | Durable activity outcomes, links, and purge paths. |
| Azure Files share | `tenant-pulse-state` | Optional persistent MSAL token cache. |
| VNet | `vnet-<prefix>` | Carries private storage traffic and Container Apps egress. |
| Infrastructure subnet | `snet-infra` | Delegated to `Microsoft.App/environments`. |
| Private endpoint subnet | `snet-pe` | Hosts storage Table and optional File private endpoints. |
| NAT gateway | `natgw-<prefix>` | Gives the VNet environment outbound access to Graph, Azure OpenAI, and ACR. |
| Public IP | `pip-<prefix>-nat` | Provides the NAT gateway's fixed outbound address. |
| Log Analytics workspace | Reused existing workspace or `log-<prefix>` | Retains console and system logs across environment recreation. |
| Azure OpenAI | Existing resource | Generates persona-aware content. |

## Why these pieces are required

### One replica

tenant-pulse is a single writer. More than one replica would schedule the same population twice,
double-post content, and race over safety limits and journal entries.

### VNet and NAT gateway

Storage public access is disabled, so the Container App must resolve and reach private endpoints
from inside the VNet. A VNet-integrated Container Apps environment has no implicit internet egress.
The NAT gateway is therefore required for image pulls, Microsoft Graph, Entra authentication, and
Azure OpenAI.

Some subscriptions require this feature before a Standard public IP can be created:

```pwsh
az feature register --namespace Microsoft.Network --name AllowBringYourOwnPublicIpAddress
az provider register --namespace Microsoft.Network --wait
```

### Durable Table journal

The activity journal uses Azure Table Storage with the Container App's managed identity and the
`Storage Table Data Contributor` role. It does not use a storage account key. This preserves every
outcome and purge path across revisions and environment replacement.

SQLite is unsuitable for Azure Files because SMB does not provide the locking behavior SQLite
requires.

### Token-cache modes

Azure Files SMB authenticates with a storage account key. There are two supported modes:

1. Shared-key auth allowed: mount the share at `/app/.state` and persist token caches.
2. Shared-key auth disabled by policy: use `-EphemeralTokenCache`, or let the deployment script
   detect `allowSharedKeyAccess=false`. Token caches stay under `/tmp` and are recreated with ROPC
   after a restart. The Table journal remains durable.

The second mode is recommended in governed subscriptions because it does not fight a policy that
will disable local storage authentication again.

## Deploy from the gitignored configuration

The example below keeps passwords and keys out of the command line. Replace the directory reader
and Azure subscription values for the target environment.

```pwsh
$config = Get-Content config\tenant-pulse.json -Raw | ConvertFrom-Json
$password = ConvertTo-SecureString `
    $config.TenantPulse.Auth.SharedPassword -AsPlainText -Force

.\scripts\deploy-azure.ps1 `
    -TenantId $config.TenantPulse.Tenant.TenantId `
    -ClientId $config.TenantPulse.Tenant.ClientId `
    -Domain $config.TenantPulse.Tenant.AllowedDomains[0] `
    -DirectoryReader <user@demo-tenant.onmicrosoft.com> `
    -SharedPassword $password `
    -SubscriptionId <azure-subscription-id> `
    -ResourceGroup rg-tenant-pulse `
    -Location eastus `
    -NamePrefix tenant-pulse `
    -PrivateNetworking `
    -EphemeralTokenCache `
    -OpenAiEndpoint $config.TenantPulse.Content.Endpoint `
    -OpenAiDeployment $config.TenantPulse.Content.Deployment `
    -CompanyName $config.TenantPulse.Content.CompanyName `
    -CompanyIndustry $config.TenantPulse.Content.CompanyIndustry
```

`-RecreateEnvironment` is required when changing an existing environment between public and
VNet-integrated networking. It deletes and recreates the Container App and environment, so export
any container-local SQLite journal before using it.

## Verify the deployment

### Replica and revision

```pwsh
az containerapp show -n ca-tenant-pulse -g rg-tenant-pulse `
    --query "{status:properties.runningStatus,revision:properties.latestReadyRevisionName}"

az containerapp replica list -n ca-tenant-pulse -g rg-tenant-pulse
```

The expected state is one ready replica with zero restarts.

The deployment script performs this readiness check itself. An accepted ARM revision is not enough:
image pulls, volume mounts, role propagation, and application startup all happen afterwards.

### Application and system logs

```pwsh
az containerapp logs show -n ca-tenant-pulse -g rg-tenant-pulse --follow
az containerapp logs show -n ca-tenant-pulse -g rg-tenant-pulse `
    --type system --tail 100
```

The Azure Activity Log contains control-plane operations such as deployments and RBAC changes. It
does not contain simulated mail, Teams, file, meeting, or Copilot activity.

Historical application logs are in the environment's Log Analytics workspace:

```kusto
ContainerAppConsoleLogs_CL
| where ContainerAppName_s == "ca-tenant-pulse"
| order by TimeGenerated desc
```

System events use `ContainerAppSystemLogs_CL`.

### Admin web

`run` can serve a small admin page from inside its own process: current stats, recent activity with
links, the creation-rate dial, and a button to generate activity for a chosen user right now.

```pwsh
.\scripts\deploy-azure.ps1 ... -AdminWeb
```

That enables ingress **and** Container Apps' built-in Entra authentication together, because the app
carries no authentication of its own. The script refuses to report success if ingress ends up open
without it.

Two things about it are deliberate:

- **It runs inside the `run` process.** The simulator is single-writer — a separate service able to
  trigger activity would double-post into the tenant and race the journal. Being in-process also
  means a manually generated batch goes through the real engine and therefore shows up in the
  journal, the console, Log Analytics and the workbook exactly like scheduled activity. Anything
  started with `az containerapp exec` never reaches Log Analytics at all.
- **Stats come from the journal, not Log Analytics.** Same data the workbook shows — the workbook's
  events are emitted from it — but with no ingestion lag and no extra RBAC.

Settings changed here are written to a `...Settings` table beside the journal and reapplied on
startup, so they survive the container being replaced on the next deployment.

Locally:

```pwsh
dotnet run --project src/TenantPulse.Cli -- run --offline --admin --admin-port 8099
```

There is no authentication in front of that, so keep it on localhost.

### Activity workbook (the easy view)

`scripts/deploy-report-workbook.ps1` deploys an Azure Workbook that reports what the simulator has
done, readable from any browser with RBAC on the workspace and pinnable to a dashboard:

```pwsh
.\scripts\deploy-report-workbook.ps1
```

It shows outcomes, activity over time, a breakdown by workload and persona, every activity with a
link straight to the artefact, failures, and engine health. Re-running updates the same workbook —
the resource name is derived from the resource group and app name, so it never duplicates.

This exists because the durable journal cannot be read from outside the VNet. Rather than opening
the storage account, the simulator **pushes** a machine-readable event per activity to stdout, which
Container Apps collects into Log Analytics automatically. `Simulation:EmitActivityEvents` controls
it and is on by default.

Each event is one line of JSON behind the marker `tenant-pulse-activity`. To query it directly:

```kusto
ContainerAppConsoleLogs_CL
| where ContainerAppName_s == "ca-tenant-pulse"
| where Log_s contains "tenant-pulse-activity"
| extend _brace = indexof(Log_s, "{")
| where _brace >= 0
| extend e = parse_json(substring(Log_s, _brace))
| project TimeGenerated,
          Kind = tostring(e.kind),
          Outcome = tostring(e.outcome),
          Persona = tostring(e.actor),
          Topic = tostring(e.topic),
          Link = tostring(e.link)
| order by TimeGenerated desc
```

Use `contains`, not `has`: `has` matches whole terms, so a hyphenated marker tokenises and produces
false positives.

**Only the main container process is collected.** Anything run through `az containerapp exec` writes
to the exec session, not to the container's stdout, so it never reaches Log Analytics. A `once`
invocation run by hand will appear in the journal and in your terminal but never in the workbook.

### Durable activity report

`report` is the authoritative view because it reads structured journal entries rather than console
text.

The storage account is normally closed to the public internet (`publicNetworkAccess: Disabled`,
often enforced by policy), so **run it inside the container** — the app's managed identity already
holds `Storage Table Data Contributor`:

```pwsh
$env:PYTHONIOENCODING='utf-8'; [Console]::OutputEncoding=[Text.Encoding]::UTF8
& 'C:\Program Files\Microsoft SDKs\Azure\CLI2\python.exe' -Bm azure.cli `
    containerapp exec -n ca-tenant-pulse -g rg-tenant-pulse `
    --command "dotnet /app/tenant-pulse.dll report --since 7"
```

Calling az's bundled `python.exe` directly avoids cmd.exe mangling the command, and
`PYTHONIOENCODING` avoids a `UnicodeEncodeError: 'charmap'` on the report's box-drawing characters.
Do not add `-I`: isolated mode makes Python ignore that variable.

Running `report` from a workstation only works where the table endpoint is reachable — an
on-network machine, or a deployment whose storage account still allows public access:

```pwsh
$env:TENANTPULSE_TenantPulse__Simulation__JournalTable__Endpoint =
    'https://<storage-account>.table.core.windows.net'

dotnet run --project src\TenantPulse.Cli -- report --since 7 --recent 20
```

The caller needs `Storage Table Data Reader`. A `403 AuthorizationFailure` is the storage *firewall*
refusing the request, not a missing role — granting the role will not help. A missing role reports
`AuthorizationPermissionMismatch` instead.

### Content generation

Every activity logging `Content generation failed ... falling back to templates` with
`403 AuthenticationTypeDisabled` means the Azure OpenAI resource has `disableLocalAuth` set and is
refusing the API key. The run continues on templates, so nothing fails visibly — only the prose gets
duller. Fix it with Entra auth rather than by re-enabling keys:

```pwsh
az cognitiveservices account show -n <aoai-name> -g rg-tenant-pulse --query properties.disableLocalAuth

az role assignment create --assignee-object-id <app-principal-id> `
    --assignee-principal-type ServicePrincipal `
    --role 'Cognitive Services OpenAI User' `
    --scope $(az cognitiveservices account show -n <aoai-name> -g rg-tenant-pulse --query id -o tsv)
```

then set `TENANTPULSE_TenantPulse__Content__UseEntraAuth=true` and drop the key. `deploy-azure.ps1`
detects `disableLocalAuth` and does all of this for you. `doctor` sends a real test prompt, so it
reports this rather than just reporting that a client could be constructed.

## Upgrade safety

Before deleting or recreating a deployment that still uses `/tmp/journal.db`:

1. Copy the SQLite database out of the running replica.
2. Run `PRAGMA integrity_check` on the copy.
3. Record its SHA-256 checksum and row count.
4. Keep the previous ACR image tag for rollback.
5. Only then recreate the environment.

The old SQLite journal is not automatically imported into Azure Table. Retain it until all
artifacts it references have been purged or are no longer needed.

## Log Analytics workspace lifecycle

Container Apps creates a random Log Analytics workspace when no workspace is supplied. Deleting the
environment does not delete that workspace, which can leave several orphaned workspaces after
repeated deployment attempts.

The deployment script now captures and reuses the current workspace before environment deletion.
For a fresh deployment it creates one stable `log-<prefix>` workspace. Delete an old workspace only
after confirming that no current Container Apps environment references its customer ID and that its
historical logs are no longer required.
