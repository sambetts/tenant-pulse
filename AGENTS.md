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
- Tests need no tenant, no network, no credentials. 41 tests, all green. A failure is yours.
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
│   ├── Journaling/                 ← SQLite journal
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
