# tenant-pulse

**Make a CDX demo tenant feel lived-in.**

A CDX demo tenant ships with ~25 licensed demo users and a pile of pre-hydrated content — and then
nothing ever happens in it again. Open Teams and the last message is from whenever the content pack
was built. Open Outlook and nothing has arrived for months. Ask Copilot what happened this week and
it has nothing to say, because nothing did.

tenant-pulse fixes that. It runs continuously and quietly gets on with a normal working day on
behalf of the demo users: emails get sent and replied to, Teams chats and channel posts appear,
documents are created and revised, meetings get booked, and Copilot actually gets used.

> **It is not a load test.** The whole point is that it looks like ~25 people doing their jobs, not
> a robot hammering an API. Default volumes are a handful of activities per person per day.

---

## Why it looks real

Most "demo data generators" produce obvious filler: the same email fifty times, lorem ipsum
documents, messages nobody would ever send. tenant-pulse is built around the three things that
actually make a tenant convincing.

**1. Storylines, not noise.** The tenant always has a few threads of real work in flight — an RFP
response, a product launch, a quarterly close, a customer escalation. Each has a cast, a beginning
and an end, and beats that play out over days across *different workloads*: the kickoff email on
Monday, the pricing document on Tuesday, the Teams chat questioning the numbers on Wednesday, the
exec sign-off on Friday. Ask Copilot about the Northwind RFP and it finds a coherent trail, because
there is one.

**2. Personas, not accounts.** Every user is modelled from their real directory attributes — job
title, department, office location. A Sales Director emails constantly and lives in meetings; an
engineer barely emails but is always in Teams and editing documents; a finance controller writes
formally and produces documents. Each has their own time zone and working hours, so activity
trickles in through the day the way it does in a real company, rather than arriving in a lump.

**3. Real words.** Content is generated per-persona and per-storyline by Azure OpenAI, told who is
writing, to whom, about what, and how formal they are. A Teams chat comes out as *"can you take a
look before 3?"*; the same person's email to the exec sponsor reads completely differently.

---

## Safety

This writes to a real tenant, so the guardrails are not optional.

| Guard | Behaviour |
| --- | --- |
| **Tenant allow-list** | Refuses to run unless the target tenant id is explicitly listed in `Tenant.AllowedTenantIds`. This is the one that stops it ever being pointed at production. |
| **Dry run by default** | Every command plans and logs but writes nothing until you pass `--live`. |
| **Rate limits** | Per-user daily cap, tenant-wide hourly cap, and a minimum gap between one person's activities. |
| **Kill switch** | Create `.state/STOP` and a running simulator stops within a minute. |
| **Full journal** | Everything it does is recorded — in SQLite locally, or an Azure Table when hosted — with the Graph path needed to delete it and a link to the artefact itself. |
| **Reversible** | `tenant-pulse purge` deletes what it created, so the tenant can be handed back clean. |
| **Marked content** | Generated mail carries an `X-TenantPulse` header — invisible in Outlook, but findable. |

---

## Try it without a tenant

You can see exactly what it would do before configuring anything:

```pwsh
dotnet run --project src/TenantPulse.Cli -- plan --offline --sample-content 6
```

That builds a synthetic 25-person company, plans a full day, and prints the schedule plus real
sample content. Nothing is contacted.

---

## Getting started

### 1. Prerequisites

- .NET 10 SDK
- A CDX demo tenant you are allowed to write to
- Admin access to that tenant (to register an app and grant consent)
- Optionally, an Azure OpenAI deployment for content generation

### 2. Configure

The fiddliest part is the Entra app registration, so there's a script for it. Sign in to the **demo**
tenant and run:

```pwsh
az login --allow-no-subscriptions --tenant <demo-tenant-id>
./scripts/setup-app-registration.ps1 -TenantId <demo-tenant-id> -IncludeCopilotExport
```

That creates a public-client app with the right delegated Graph scopes, grants admin consent, and
writes a starter `config/tenant-pulse.json` with the tenant already on the allow-list.

Prefer to do it by hand? Copy the example and fill it in:

```pwsh
Copy-Item config/tenant-pulse.example.json config/tenant-pulse.json
```

Either way, **the tenant id must appear in `AllowedTenantIds`** — it will refuse to run otherwise.
Keep secrets out of the file if you prefer:

```pwsh
$env:TENANTPULSE_AOAI_KEY = "<azure-openai-key>"
$env:TENANTPULSE_SHARED_PASSWORD = "<shared demo user password>"   # only for UsernamePassword mode
```

### 3. Check

```pwsh
dotnet run --project src/TenantPulse.Cli -- doctor
```

`doctor` tells you precisely what is missing and what would happen. Run it whenever something stops
working.

### 4. Enrol the users

Activity has to be performed **as each user** — app-only Graph cannot post Teams messages or call
Copilot at all, and app-only mail/file writes are not attributed to the user in the usage reports.
So each demo user signs in once, and their refresh token is cached (encrypted) from then on.

```pwsh
# Supported route: one device-code sign-in per user
dotnet run --project src/TenantPulse.Cli -- bootstrap --user megan@M365x000000.onmicrosoft.com
dotnet run --project src/TenantPulse.Cli -- bootstrap --all --as megan@M365x000000.onmicrosoft.com
```

If your demo tenant allows it, set `Auth.Mode` to `UsernamePassword` and all 25 enrol unattended.
That flow is deprecated by Microsoft and blocked by MFA/Conditional Access/security defaults, but
demo tenants usually permit it. See [docs/auth.md](docs/auth.md).

### 5. Look before you leap

```pwsh
dotnet run --project src/TenantPulse.Cli -- plan --days 3
```

### 6. Go

```pwsh
dotnet run --project src/TenantPulse.Cli -- once --count 3 --live   # prove it end to end
dotnet run --project src/TenantPulse.Cli -- run --live              # leave it running
```

### 7. Optional: run it in Azure instead of on your machine

`run` keeps going by itself — it rolls over at midnight UTC and plans the next day — so "daily
activity" really just means keeping the process alive. Leaving that to a laptop means the tenant
goes quiet whenever the laptop does.

```pwsh
./scripts/deploy-azure.ps1 `
    -TenantId <demo-tenant-id> `
    -ClientId <app-client-id> `
    -Domain M365x000000.onmicrosoft.com `
    -DirectoryReader someone@M365x000000.onmicrosoft.com `
    -OpenAiEndpoint https://<your-resource>.openai.azure.com/
```

That builds the image in Azure (no local Docker), then deploys it to Container Apps on a single
always-on replica. Roughly £10–15/month.

A few things worth knowing:

| | |
| --- | --- |
| **No secrets in the image** | The Azure OpenAI key and shared password are Container Apps secrets, injected at run time. |
| **It enrols itself** | There is no token cache to copy up. With `Auth.Mode=UsernamePassword` the container signs each user in on demand, so it recovers from a cold start on its own. |
| **One replica, always** | The simulator is single-writer. Two replicas would double-post into the tenant. |
| **Token caches are unencrypted there** | No DPAPI or keyring exists in a container, so MSAL falls back to plain files. They hold refresh tokens — keep that path private. |

### Durable state needs `-PrivateNetworking`, which needs an egress path

By default the deployment mounts nothing and keeps the journal in SQLite on container-local disk.
That runs, but the journal does not survive a restart — so `purge` cannot clean up afterwards, and
there is nothing for `report` to read remotely. The console log is the only record.

Pass `-PrivateNetworking` for the durable version: a VNet, private endpoints onto the storage
account, and the journal in an Azure Table written with the app's managed identity. Then `report`
and `purge` work from anywhere with line of sight to the endpoint:

```pwsh
$env:TENANTPULSE_TenantPulse__Simulation__JournalTable__Endpoint = 'https://<account>.table.core.windows.net'

dotnet run --project src/TenantPulse.Cli -- report --since 7 --recent 20
dotnet run --project src/TenantPulse.Cli -- report --persona megan@M365x000000.onmicrosoft.com
dotnet run --project src/TenantPulse.Cli -- purge --since 30 --live
```

`report` lists every activity with a link to the mail, document, meeting or Teams message it
produced, so a claim can be checked in a browser rather than taken on trust. Failures are always
shown. Reading needs the **Storage Table Data Reader** role; `purge` marks rows, so it needs
**Storage Table Data Contributor**.

Some governed subscriptions enforce `allowSharedKeyAccess=false` on storage accounts. Azure Files
SMB mounts use an account key, so the token-cache volume cannot start under that policy. The deploy
script detects this and leaves token caches on container-local disk while keeping the Azure Table
journal durable. `Auth.Mode=UsernamePassword` re-enrols users after a restart. Pass
`-EphemeralTokenCache` to request this mode explicitly.

**The catch.** A VNet-integrated Container Apps environment has no outbound internet unless you give
it one, and this simulator needs plenty — Microsoft Graph, Azure OpenAI, and the image pull itself.
That normally means a NAT gateway, which needs a public IP. In a governed subscription that forbids
public IPs, `-PrivateNetworking` cannot work: the environment cannot pull so much as
`mcr.microsoft.com`, and fails with `Deployment Progress Deadline Exceeded. 0/1 replicas ready` and
no other explanation. Meanwhile a policy that forces `publicNetworkAccess: Disabled` on storage
means a *non*-VNet environment cannot reach the account at all — and a volume pointing at an
unreachable share puts the container into `CrashLoopBackOff` with no log output whatsoever. Those
two policies together leave no way to have both durable state and a working simulator; sort out
egress first.

If Azure reports `SubscriptionNotRegisteredForFeature` while creating the NAT public IP:

```pwsh
az feature register --namespace Microsoft.Network --name AllowBringYourOwnPublicIpAddress
az provider register --namespace Microsoft.Network --wait
```

See [`docs/azure-deployment.md`](docs/azure-deployment.md) for the resource map, rationale,
verification commands, upgrade safety, and Log Analytics guidance.

---

## Commands

| Command | What it does |
| --- | --- |
| `doctor` | Pre-flight check: config, allow-list, enrolment, licences, content provider. |
| `bootstrap` | Enrol demo users so they can act. |
| `plan` | Show what would happen. Always read-only. `--offline` needs no tenant. |
| `run` | Run continuously, acting as each scheduled moment arrives. |
| `once` | Execute a few activities immediately — the fastest end-to-end proof. |
| `verify-copilot` | Prove empirically whether API-driven Copilot prompts register as real usage. |
| `report` | Summarise what has been done — totals, per persona, failures, and links to what it created. |
| `purge` | Delete what tenant-pulse created. |

Run `tenant-pulse help` for the full option list.

---

## About Copilot

Copilot is the interesting part, and the part with a genuine open question.

Prompts are sent through the Graph **Copilot Chat API** (`/beta/copilot/conversations`) as the user.
It is delegated-only and needs a Microsoft 365 Copilot licence per user. What Microsoft does *not*
document is whether prompts sent this way are counted in the admin centre's Copilot usage reports.

Rather than guess, tenant-pulse finds out:

```pwsh
dotnet run --project src/TenantPulse.Cli -- verify-copilot --live --user <upn> --app-token <jwt>
```

It sends a uniquely-marked prompt, waits, then reads that user's history back through the Copilot
Interaction Export API looking for the marker. On the reference CDX tenant the marker **was** found,
so API prompts do reach the enterprise interaction store — but that is not quite the same surface as
the admin centre's next-day active-users report, and this is a preview API. Re-run it per tenant.
See [docs/copilot.md](docs/copilot.md) for what to do with each answer.

Copilot Studio agents are driven over the Direct Line API, which is stable, documented and does show
up in Copilot Studio analytics.

---

## How it fits together

```
 config/storylines.json ──► StorylineScheduler ──► CastingDirector
                                    │                    │  (roles → real people)
 Entra directory ──► GraphPersonaDirectory ──► Persona[] ─┘
                                    │
                                    ▼
                              DayPlanner  ──► ActivityIntent[]   (who, what, when, about what)
                                    │
                                    ▼
                             PulseEngine  ──► SafetyGovernor ──► allow-list, caps, kill switch
                                    │
                          ┌─────────┴──────────┐
                          ▼                    ▼
                  IContentGenerator      IActivityExecutor
                  (Azure OpenAI /        (mail, Teams, files,
                   templates)             calendar, Copilot, agents)
                          │                    │
                          └─────────┬──────────┘
                                    ▼
                             Microsoft Graph  (delegated, per user)
                                    │
                                    ▼
                            activity journal ──► report / purge
                          (SQLite, or Azure Table when hosted)
```

| Project | Role |
| --- | --- |
| `TenantPulse.Core` | Domain: personas, storylines, planning, safety. No I/O. |
| `TenantPulse.Engine` | Everything that talks to something: Graph, MSAL, Azure OpenAI, SQLite. |
| `TenantPulse.Cli` | The `tenant-pulse` command. |
| `TenantPulse.Tests` | Unit tests (xUnit v3, NSubstitute, AwesomeAssertions). |
| `Dockerfile` | Container image for running the simulator in Azure. Carries no configuration or secrets. |

Storylines are **data**, not code — add your own to `config/storylines.json` to give a tenant an
industry-appropriate narrative without touching the build.

---

## Build and test

```pwsh
cd src
dotnet build TenantPulse.slnx
dotnet test TenantPulse.Tests/TenantPulse.Tests.csproj
```

Warnings are errors. Tests need no tenant, network or credentials.

---

## Docs

- [docs/auth.md](docs/auth.md) — why delegated tokens, and how to enrol 25 users
- [docs/copilot.md](docs/copilot.md) — Copilot and agents, and the usage-reporting question
- [docs/storylines.md](docs/storylines.md) — writing your own storylines
- [AGENTS.md](AGENTS.md) — orientation for AI agents working on this repo

## Licence

MIT.
