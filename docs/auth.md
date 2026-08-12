# Authentication

## Why every activity needs a delegated (per-user) token

This is the single most important design constraint in tenant-pulse, so it's worth being explicit.

App-only (client credentials) Graph access would be far easier to operate — one secret, no
enrolment, no expiry. It is also useless here, for two separate reasons:

**1. Several activities are technically impossible app-only.**

| Activity | Why app-only fails |
| --- | --- |
| Teams 1:1 and group chat messages | `ChatMessage.Send` is delegated-only. The app-only path is import/migration mode, which is not the same thing. |
| Teams channel messages | Same. |
| Microsoft 365 Copilot prompts | The Copilot Chat API is delegated-only by design. |
| Copilot Retrieval API | Delegated-only. |

**2. Where it is possible, it doesn't count.**

The Microsoft 365 admin centre usage reports measure *user* activity. An app-only `sendMail` on
behalf of a user does not mark that user active in the Exchange activity report — Microsoft's
`getEmailAppUsageUserCounts` documentation describes counting users who *connected using an email
app*, and service principals are not that.

Since the entire purpose is a tenant that looks used, an approach that generates content but leaves
every usage report flat would defeat the object. So: delegated tokens, one per simulated user.

---

## Enrolment routes

### Device code (default, supported)

Each user signs in once, interactively. MSAL caches the refresh token, encrypted at rest by the
platform (DPAPI on Windows, keychain on macOS, keyring on Linux), in `Auth.CacheDirectory`. After
that, tokens are acquired silently and no human is involved again.

```pwsh
tenant-pulse bootstrap --user adele@M365x000000.onmicrosoft.com
```

You will be shown a code to enter at <https://microsoft.com/devicelogin>. Sign in **as that user**,
not as yourself — tenant-pulse warns if the token comes back for a different account.

Enrolling 25 users this way is tedious but is a one-off. Once one user is enrolled, they can read
the directory so the rest can be discovered automatically:

```pwsh
tenant-pulse bootstrap --all --as adele@M365x000000.onmicrosoft.com
```

### Username / password (ROPC) — opt in

CDX tenants typically provision every demo user with the same password and no MFA, which makes
unattended enrolment of all 25 users a single command:

```jsonc
"Auth": {
  "Mode": "UsernamePassword",
  "SharedPassword": null   // prefer the TENANTPULSE_SHARED_PASSWORD environment variable
}
```

```pwsh
$env:TENANTPULSE_SHARED_PASSWORD = "<the demo password>"
tenant-pulse bootstrap --all --as admin@M365x000000.onmicrosoft.com
```

**Understand what you're opting into.** ROPC is deprecated: MSAL.NET marks it obsolete, RFC 9700
states it "MUST NOT be used", and it is incompatible with MFA, Conditional Access and Entra security
defaults. It works in most demo tenants today and may stop working at any time. If most users fail
to enrol, that is the signal — switch to `DeviceCode`.

tenant-pulse never stores a password: it exchanges it for a refresh token and caches only that.

---

## The app registration

The quickest route is `./scripts/setup-app-registration.ps1 -TenantId <id> -IncludeCopilotExport`,
which does everything below and writes a starter config. To do it by hand, register a **public
client** application in the demo tenant:

- Authentication → Advanced settings → **Allow public client flows: Yes**
  (required for both device code and ROPC)
- API permissions → Microsoft Graph → **Delegated**, then grant admin consent:

```
User.Read                 User.ReadBasic.All
Mail.ReadWrite            Mail.Send
Chat.ReadWrite            ChannelMessage.Send        ChannelMessage.Read.All
Team.ReadBasic.All        Channel.ReadBasic.All
Files.ReadWrite.All       Sites.ReadWrite.All
Calendars.ReadWrite
```

Admin consent matters: without it every user would be prompted individually at enrolment.

For `verify-copilot` you additionally need an **application** permission,
`AiEnterpriseInteraction.Read.All`, because the Copilot Interaction Export API is app-only. That is
the only place tenant-pulse uses an app-only token, and you pass it in explicitly.

---

## Token expiry

Refresh tokens last as long as the tenant's policies allow — typically up to 90 days on a sliding
window in a permissive demo tenant, and often until explicitly revoked.

When one expires, that persona's activities are skipped with a clear message rather than failing the
run. `tenant-pulse doctor` reports how many personas currently have a usable token, and re-running
`bootstrap` for that user fixes it.

If a Conditional Access policy enforcing sign-in frequency or MFA is later applied to the tenant, all
cached tokens stop working and device-code re-enrolment becomes necessary.

---

## Handling the caches

`Auth.CacheDirectory` (default `.state/token-cache`) contains a refresh token for every simulated
user. Treat it as a secret store:

- It is gitignored, and should stay that way.
- Don't copy it between machines — the platform encryption is machine-scoped.
- To revoke everything, delete the directory and, if it matters, reset the users' passwords or revoke
  their sessions in Entra.
