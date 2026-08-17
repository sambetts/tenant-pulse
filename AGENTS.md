# Agent context — tenant-pulse

> Orientation for the next Copilot / agent session. Read this first.

## The 30-second summary

tenant-pulse simulates realistic user activity in a **CDX demo tenant** (a throwaway Microsoft 365
demo tenant with ~25 licensed demo users) so the tenant feels lived-in for demos. It runs
continuously, acting **as each user**, generating mail, Teams messages, documents, meetings and
Copilot prompts, driven by persona models and multi-day storylines.

It is **not** a load test. Realism beats throughput, always.

## The invariants you must never break

> **1. tenant-pulse must never act against a tenant that is not explicitly allow-listed.**

Enforced by `SafetyGovernor.AssertTenantAllowed()` (`Core/Safety/SafetyGovernor.cs`), which throws
`TenantNotAllowedException` unless `Tenant.TenantId` appears in `Tenant.AllowedTenantIds`. Every
command that can write calls it before anything else. `SafetyGovernorTests` locks this down. Do not
add a code path that reaches an executor without passing through it.

> **2. Dry run is the default.**

`Simulation.DryRun` defaults to `true`; only `--live` clears it. Every executor must honour
`ExecutionContext.DryRun` and return `ActivityResult.Simulated(...)` rather than writing.

> **3. Everything written must be journalled with a purge path.**

`ActivityResult.Executed(resourceId, purgePath, detail)` — the purge path is what makes the
simulator reversible. If you add an executor that creates something deletable, record how to delete
it.

## Build, test

```pwsh
cd src
dotnet build TenantPulse.slnx -v minimal          # use the .slnx
dotnet test TenantPulse.Tests/TenantPulse.Tests.csproj
```

- `TreatWarningsAsErrors=true` and `EnforceCodeStyleInBuild=true` — **any warning fails the build**.
  Fix it, don't suppress it.
- Tests need no tenant, no network, no credentials. 54 tests, all green. A failure is yours.
  The Azure Table journal tests are the one exception and they skip themselves unless the storage
  emulator is up: `npm i -g azurite` then `azurite-table --silent --location <dir>`.
- Fastest end-to-end check with no tenant:
  `dotnet run --project src/TenantPulse.Cli -- plan --offline --sample-content 6`

## Layout

```
src/
├── TenantPulse.slnx
├── Directory.Packages.props        ← central package management (NO Version= in csproj)
├── TenantPulse.Core/               ← domain, no I/O
│   ├── Personas/                   ← Persona, traits, working hours, IUserTokenProvider
│   ├── Storylines/                 ← Storyline, CastingDirector, StorylineScheduler
│   ├── Scheduling/DayPlanner.cs    ← THE realism engine (see below)
│   ├── Safety/SafetyGovernor.cs    ← allow-list, rate limits, kill switch
│   ├── Activities/                 ← ActivityIntent/Result/Kind, IActivityExecutor
│   ├── Content/                    ← IContentGenerator contracts
│   └── Configuration/              ← TenantPulseOptions
├── TenantPulse.Engine/             ← everything that talks to something
│   ├── Auth/                       ← MSAL broker + encrypted per-user token caches
│   ├── Graph/                      ← lean typed HTTP client (deliberately NOT the Graph SDK)
│   ├── Activities/                 ← mail / Teams / files / calendar executors
│   ├── Copilot/                    ← Copilot Chat API, Direct Line agents, usage verifier
│   ├── Content/                    ← Azure OpenAI + template generators
│   ├── Personas/                   ← directory loader + synthetic workforce for --offline
│   ├── Journaling/                 ← SQLite + Azure Table journals
│   └── PulseEngine.cs              ← the run loop
├── TenantPulse.Cli/Commands/       ← doctor, bootstrap, plan, run, once, verify-copilot, report, purge
└── TenantPulse.Tests/
config/storylines.json              ← storylines are DATA, not code
```

## Design decisions you should not casually reverse

**Delegated tokens, never app-only.** App-only Graph cannot post Teams messages or call Copilot at
all, and app-only mail/file writes are not attributed to the user in the M365 usage reports — so an
app-only simulator would leave every usage report flat, defeating the point. This is why there is a
per-user MSAL cache and an enrolment step. See `docs/auth.md`.

**No Microsoft Graph SDK.** We use a lean typed `HttpClient` (`Engine/Graph/GraphClient.cs`). The
Beta SDK is enormous, and it does not model the preview Copilot endpoints we need. Don't "modernise"
this by adding the SDK.

**The Copilot Chat API is preview and its usage-reporting behaviour is only partly established.** A
`verify-copilot` run against a CDX tenant (2026-08-12) did find its marker in the enterprise
interaction store, so prompts sent via `/beta/copilot/conversations` are recorded there. That is not
a licence to claim they appear in the admin centre's daily active-users report — a different,
next-day surface. Keep that distinction in code and docs, and re-run `verify-copilot` per tenant
rather than asserting the result.

**ROPC is opt-in and deprecated.** `AuthMode.UsernamePassword` exists because it enrols 25 demo
users unattended and demo tenants usually allow it. It is guarded by `#pragma warning disable CS0618`
with an explanatory comment. Don't promote it to the default; don't delete it either.

**Storylines are data.** Adding a business scenario should never require a rebuild.

## `DayPlanner` — where realism lives

`Core/Scheduling/DayPlanner.cs` is the file most likely to be the answer when someone says "it
doesn't feel real". It merges:

1. **Storyline beats** — scripted, coherent, cross-workload, same cast over days.
2. **Ambient activity** — background hum sized by each persona's traits.

Things it does deliberately, which you should not undo:

- Places activity inside each persona's **own working hours in their own time zone**, avoiding lunch,
  using a **two-hump distribution** (mid-morning and mid-afternoon busy periods) rather than uniform
  random.
- Handles DST gaps and ambiguous times when converting persona-local wall clock to UTC.
- Gives ambient topics a **specific angle** (`"Quarterly close — chasing the outstanding numbers"`),
  never the bare storyline title repeated — repetition is what makes generated content look
  generated.
- **Spaces one persona's activities apart** (`EnforcePerPersonaSpacing`) so they don't collide on the
  same minute and get thrown away later by the governor's minimum-gap rule.
- Prefers colleagues in the **same department** as targets.

## Conventions

| Convention | Notes |
| --- | --- |
| Central package management | Versions in `Directory.Packages.props`. Never `Version=` in a csproj. |
| File-scoped namespaces | Everywhere. |
| `ExecutionContext` collision | `TenantPulse.Core.Activities.ExecutionContext` clashes with `System.Threading.ExecutionContext`. Alias it: `using ExecContext = TenantPulse.Core.Activities.ExecutionContext;` |
| Logging | `ILogger<T>` via constructor. No static loggers. |
| Executors never throw for expected conditions | Nothing to reply to, no team, no licence → `ActivityResult.Skipped(reason)`. Only genuine faults are `Failed`. |
| Determinism | All randomness goes through `DeterministicRandom.For(seed, keyParts)` so a seed replays a run. Don't introduce bare `new Random()`. |
| Secrets | `config/tenant-pulse.json`, `.state/` and `*.db` are gitignored. Never commit a tenant id with credentials, a password, or a token cache. |

## Gotchas already paid for

- **SQLite cannot run on an Azure Files SMB share.** The byte-range locking it needs is unsupported,
  and every statement fails with `SQLite Error 5: 'database is locked'`. Container Apps gives no
  control over mount options, so `nobrl` is not available either. Hence
  `Simulation.JournalSnapshotPath`: the live journal runs on container-local disk and is copied to
  the share with `VACUUM INTO` (staged locally, then a plain file copy — SMB handles streams fine,
  just not locks). Without that snapshot a restart would lose every purge path.
  **The hosted deployment no longer relies on any of this** — it uses
  `Simulation.JournalTable` (Azure Table), which is durable, readable from anywhere, and needs no
  snapshot. The SQLite path and its snapshot remain for local use.
- **A file share whose storage account has `publicNetworkAccess: Disabled` silently fails to
  mount.** The container still starts, `/app/.state` is just the image's own empty directory, and
  every snapshot "succeeds" onto disposable local disk. Nothing errors; durability simply stops.
  If state ever looks missing, check `mount | grep cifs` inside the container first.
- **A *newly created* environment with an unreachable share does not degrade so politely** — it
  goes into `CrashLoopBackOff` with **no console output at all**, which looks like a broken image.
  If the container dies before .NET logs anything, suspect the volume. Hence the deploy script only
  mounts the share under `-PrivateNetworking`.
- **Container Apps + VNet + private endpoints is all-or-nothing, and needs egress.** A VNet
  environment has no outbound internet unless something provides it, and this simulator needs Graph,
  Azure OpenAI and the image pull. Without a NAT gateway the environment cannot pull
  `mcr.microsoft.com`, and the only symptom is `Deployment Progress Deadline Exceeded. 0/1 replicas
  ready` — no pull error, no mount error. Diagnose it by deploying a stock public image into the
  same environment; if that will not start either, it is egress, not the app.
- **Governed subscriptions can make this unsolvable.** On the reference subscription,
  `Microsoft.Network/AllowBringYourOwnPublicIpAddress` is not registered, so no public IP means no
  NAT gateway means no VNet egress; and a policy re-disables `publicNetworkAccess` on storage within
  seconds of it being enabled, so a non-VNet environment cannot reach storage either. Verify both
  before promising durable state: `az network public-ip create` and
  `az storage account update --public-network-access Enabled` are the two-command test. ARM
  deployment validation can still succeed while the public-IP feature is unregistered, so inspect
  `az feature show` explicitly.
- **Storage account keys are not a safe foundation in a governed subscription.** A
  `StorageAccount_DisableLocalAuth` policy can switch shared-key access off underneath a working
  deployment, so the journal table authenticates with the app's managed identity
  (`JournalTable.Endpoint` + `DefaultAzureCredential`). `JournalTable.ConnectionString` exists for
  the emulator and local runs. The same policy also prevents the Azure Files SMB token-cache mount
  from authenticating. `deploy-azure.ps1` detects that state and keeps token caches on local disk;
  ROPC re-enrols users after restart while the Table journal remains durable.
- **Container Apps environment recreation can leak Log Analytics workspaces.** If no workspace is
  supplied, Azure creates a random one and does not delete it with the environment. The deployment
  script captures and reuses the current workspace before recreation, or creates one stable
  `log-<prefix>` workspace for a fresh deployment.
- **An accepted Container App revision is not a healthy deployment.** ARM can report success while
  the replica is still failing image pulls or Azure Files mounts. The deployment script waits for
  exactly one ready replica and prints recent system events before it reports success.
- **`az containerapp env delete` returns before the deletion completes.** Creating over the top
  fails with `ManagedEnvironmentScheduledForDelete`, and because `az` failures do not stop a
  PowerShell script the rest of the deployment then runs against nothing and still reports success.
  The script polls for the deletion and asserts each resource exists afterwards.
- **`az containerapp exec` from Windows is doubly awkward.** `az.cmd` hands the command to cmd.exe,
  which mangles nested quotes (`'sed' is not recognized`) — call az's bundled `python.exe` directly
  instead, and keep the `--command` free of nested quotes. Its output decoder then dies with
  `UnicodeEncodeError: 'charmap'` on any non-ASCII the container prints, which `report` is full of.
  The fix is `PYTHONIOENCODING`, but **it must not be run under `-I`**: isolated mode implies `-E`,
  so Python ignores the variable and the crash looks unfixable. Use `-Bm`:

  ```pwsh
  $env:PYTHONIOENCODING='utf-8'; [Console]::OutputEncoding=[Text.Encoding]::UTF8
  & 'C:\Program Files\Microsoft SDKs\Azure\CLI2\python.exe' -Bm azure.cli `
      containerapp exec -n ca-tenant-pulse -g rg-tenant-pulse `
      --command "dotnet /app/tenant-pulse.dll report --since 7"
  ```
- **The hosted journal cannot be read from a workstation.** `docs/azure-deployment.md` shows
  `report` pointed at `JournalTable__Endpoint`, which works only from inside the VNet. CDX-style
  governed subscriptions keep `publicNetworkAccess: Disabled` on the storage account, so a laptop
  gets `403 AuthorizationFailure` — a *network* refusal that looks like an RBAC problem and is not
  fixed by granting `Storage Table Data Reader`. Run `report` inside the container instead, or use
  the workbook below.
- **Reporting pushes out, it is not reached in for.** Because the journal is private,
  `ActivityEventLog` writes one line of JSON per activity to stdout behind the marker
  `tenant-pulse-activity`; Container Apps collects it into Log Analytics, which is readable from any
  browser with RBAC. `scripts/deploy-report-workbook.ps1` deploys the workbook over it. Three things
  that will bite:
  - **Only the main container process is collected.** `az containerapp exec` output goes to the exec
    session, so a hand-run `once` never appears in Log Analytics — only in the journal.
  - **Use `contains`, not `has`, for the marker.** `has` matches whole terms, so a hyphenated string
    tokenises and matches unrelated lines.
  - **`last` is a reserved word in KQL.** `summarize ..., last = max(TimeGenerated)` fails with a
    bare `SYN0002` that looks like quoting damage. Rename the column.
- **The payload must stay one line and pure ASCII.** The log collector emits one row per line, so a
  wrapped event is unparseable, and the Windows `az` log viewer dies on any non-ASCII byte. The
  default `System.Text.Json` encoder escapes both; `ActivityEventLogTests` pins it.
- **No DPAPI or keyring exists in a container**, so `TokenCacheStore` falls back to an unencrypted
  MSAL cache. It still holds refresh tokens for every user — the volume must stay private.
- **`run` needs a directory reader to start.** With an empty cache there is no enrolled user, so the
  container passes `--as <upn>`; ROPC then enrols everyone lazily. Without it the container
  crash-loops on "No enrolled user is available to read the directory".
- **Repository PowerShell scripts must be ASCII-only.** They have no BOM, so Windows PowerShell 5.1
  reads them as ANSI and an em dash becomes a parse error. Scheduled tasks and hooks hit this even
  when PowerShell 7 runs them fine interactively.
- **A misconfigured Azure OpenAI must not break read-only commands.** `AddContentGeneration` in
  `ServiceRegistration.cs` try-constructs the Azure generator and silently falls back to templates.
  Before that, `plan` and `doctor` crashed with a DI resolution error when no endpoint was set.
- **Azure OpenAI needs Entra auth in a governed subscription.** `disableLocalAuth` is commonly set,
  so every API key is refused with `403 AuthenticationTypeDisabled`. Because generation falls back
  to templates the run looks healthy and the content merely gets blander, which is why this went
  unnoticed for days. Set `Content:UseEntraAuth` (or configure no key at all) and grant the app's
  managed identity `Cognitive Services OpenAI User` on the resource. `deploy-azure.ps1` detects
  `disableLocalAuth` and does both automatically. Note that *constructing* the client proves
  nothing — `doctor` sends a real test prompt via `AzureOpenAIContentGenerator.ProbeAsync`.
- **Low weekend volume is correct, not a fault.** A representative week planned 160 activities on
  the Friday, 1 on the Saturday, 3 on the Sunday and 162 on the Monday. Before investigating a
  "collapse" in activity, check the day of the week.
- **`NSubstitute` is pinned to 6.1.0** — 6.2.0 is on nuget.org but not on the internal feed this
  machine restores from.
- **The template generator must split `"Storyline — angle"` topics** (`Topic()` / `Focus()` helpers)
  or it emits "notes on X, following the latest work on X".
- **Copilot activity is only planned for licensed users.** The planner checks
  `Persona.HasCopilotLicence`, which comes from `assignedPlans` containing a service matching
  "Copilot". Unlicensed users get 403 from every Copilot endpoint.

## When adding an activity kind

1. Add to `ActivityKind` and map it in `ActivityKindExtensions.ToWorkload()`.
2. Implement `IActivityExecutor` in `Engine/Activities/`, honouring dry run and returning a purge
   path.
3. Register it in `ServiceRegistration.AddExecutors()`.
4. Give it a weight in `DayPlanner.PickAmbientKind()` if it should occur ambiently.
5. Add a `ContentShape` and prompt handling if it needs generated words.
6. Cover the planning behaviour in `TenantPulse.Tests`.
