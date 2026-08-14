# Copilot instructions — tenant-pulse

tenant-pulse simulates realistic user activity in a **CDX demo tenant** (a throwaway Microsoft 365
demo tenant, ~25 licensed demo users) so it feels lived-in for demos: mail, Teams, documents,
meetings and Copilot prompts, generated continuously from persona + storyline models and executed
**as each user** through Microsoft Graph.

It is **not** a load test. Realism beats throughput. See `AGENTS.md` for full orientation,
`docs/auth.md`, `docs/copilot.md`, `docs/storylines.md`, and `docs/azure-deployment.md` for the deep
detail.

## Invariants you must never break

1. **Never act against a non-allow-listed tenant.** `SafetyGovernor.AssertTenantAllowed()` throws
   unless `Tenant.TenantId` is in `Tenant.AllowedTenantIds`. Every write path calls it first.
2. **Dry run is the default.** `Simulation.DryRun` starts `true`; only `--live` clears it. Executors
   must honour `ExecutionContext.DryRun` and return `ActivityResult.Simulated(...)`.
3. **Everything created must be journalled with a purge path**, so `purge` can hand the tenant back
   clean.

## Build and test

```pwsh
cd src
dotnet build TenantPulse.slnx -v minimal
dotnet test TenantPulse.Tests/TenantPulse.Tests.csproj
```

`TreatWarningsAsErrors=true` + `EnforceCodeStyleInBuild=true` — any warning fails the build; fix it,
never suppress. Tests need no tenant/network/credentials (54 total; 7 Azure Table tests skip unless
Azurite is running).

No-tenant end-to-end check:
`dotnet run --project src/TenantPulse.Cli -- plan --offline --sample-content 6`

## Architecture

`TenantPulse.Core` (domain, no I/O) → `TenantPulse.Engine` (Graph, MSAL, Azure OpenAI, SQLite) →
`TenantPulse.Cli` (the `tenant-pulse` command).

```
storylines.json → StorylineScheduler → CastingDirector ─┐
Entra directory → GraphPersonaDirectory → Persona[] ────┴→ DayPlanner → ActivityIntent[]
                                                              ↓
                                    PulseEngine → SafetyGovernor → IActivityExecutor
                                                              ↓          ↑ IContentGenerator
                                                     Graph (delegated) → SQLite / Azure Table journal
```

## Decisions not to casually reverse

- **Delegated (per-user) tokens, never app-only.** App-only cannot post Teams messages or call
  Copilot, and app-only mail/file writes aren't attributed to the user in M365 usage reports — the
  tenant would stay looking unused. This is why enrolment exists.
- **No Graph SDK.** A lean typed `HttpClient` (`Engine/Graph/GraphClient.cs`); the Beta SDK is huge
  and doesn't model the preview Copilot endpoints.
- **Don't claim Copilot Chat API prompts count as usage.** Microsoft doesn't document it; the
  `verify-copilot` command establishes it empirically. Keep that uncertainty honest in code and docs.
- **ROPC (`AuthMode.UsernamePassword`) is opt-in, deprecated and deliberately kept** — it enrols 25
  demo users unattended. Don't make it the default; don't delete it.
- **Storylines are data** (`config/storylines.json`), never code.

## Conventions that will bite you

- **Central package management** — versions live in `src/Directory.Packages.props`; never put
  `Version=` on a `PackageReference`.
- **File-scoped namespaces** everywhere.
- **`ExecutionContext` collides** with `System.Threading.ExecutionContext`. Alias it:
  `using ExecContext = TenantPulse.Core.Activities.ExecutionContext;`
- **Executors never throw for expected conditions** — nothing to reply to, no team, no licence all
  return `ActivityResult.Skipped(reason)`. Only genuine faults are `Failed`, because the loop has to
  survive for days.
- **All randomness goes through `DeterministicRandom.For(seed, keyParts)`** so a seed replays a run.
  Never `new Random()`.
- **Secrets**: `config/tenant-pulse.json`, `.state/`, `*.db` are gitignored. Never commit a password,
  token cache or real tenant id with credentials.
- **Hosted deployments use Azure Table for the durable activity journal.** In governed
  subscriptions that force `allowSharedKeyAccess=false`, Azure Files SMB mounts fail with
  `VolumeMountFailure`; keep token caches on `/tmp` and let ROPC re-enrol after restart.
- **Private Container Apps environments require NAT egress.** Confirm
  `Microsoft.Network/AllowBringYourOwnPublicIpAddress` is registered before replacing an
  environment. ARM template validation alone does not catch that subscription feature gate.
- **Never treat an accepted Container App revision as a successful deployment.** Wait for one ready
  replica and inspect `--type system` logs for image-pull or volume-mount failures.
- **Reuse the Log Analytics workspace when recreating an environment.** Azure otherwise creates a
  random workspace and leaves the previous one orphaned.
- **A misconfigured Azure OpenAI must never break read-only commands** — `AddContentGeneration`
  try-constructs and falls back to templates (this was a real crash in `plan`/`doctor`).
- **`NSubstitute` is pinned to 6.1.0** — 6.2.0 isn't on the internal NuGet feed.

## Realism lives in `Core/Scheduling/DayPlanner.cs`

If something "doesn't feel real", start there. It deliberately: places activity in each persona's own
working hours and time zone (avoiding lunch, two-hump daily distribution, DST-safe); gives ambient
topics a specific angle rather than repeating the storyline title; spaces one persona's activities
apart so they don't collide; and prefers same-department colleagues as targets. Don't flatten any of
that into uniform random.

## Adding an activity kind

`ActivityKind` + `ToWorkload()` → implement `IActivityExecutor` (dry-run + purge path) → register in
`ServiceRegistration.AddExecutors()` → weight it in `DayPlanner.PickAmbientKind()` → add a
`ContentShape` if it needs words → cover the planning behaviour in tests.
