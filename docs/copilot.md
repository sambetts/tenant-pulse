# Copilot and Copilot agents

Getting Copilot to look used is the most valuable and least settled part of tenant-pulse. This
document is deliberately honest about what is known, what is assumed, and how to find out.

---

## Microsoft 365 Copilot prompts

tenant-pulse sends prompts through the Graph **Copilot Chat API**:

```
POST /beta/copilot/conversations                        → { id }
POST /beta/copilot/conversations/{id}/chat              → { messages: [ …, answer ] }
```

The chat call requires a `locationHint.timeZone` — without it the call is rejected with
`400 badRequest`. tenant-pulse sends the acting persona's own time zone:

```json
{ "message": { "text": "…" }, "locationHint": { "timeZone": "Europe/London" } }
```

Facts:

- It is **preview**, on `/beta`. The endpoint shape has changed before and may change again.
- It is **delegated-only** — application permissions are explicitly unsupported.
- Each calling user needs a **Microsoft 365 Copilot licence**. Without one, every call returns 403.
  The planner only schedules Copilot activity for users it can see a Copilot service plan on, so
  unlicensed users are skipped rather than failing.
- It grounds answers in the user's own content, and so demands the **full set of read scopes** in
  the token: `Mail.Read`, `Chat.Read`, `Sites.Read.All`, `People.Read.All`, `ChannelMessage.Read.All`,
  `ExternalItem.Read.All` and `OnlineMeetingTranscript.Read.All`. A missing one gives 403 listing
  them all — and a broader scope does not substitute, so `Mail.ReadWrite` alone will not do.
- Copilot licences cannot be detected from `assignedPlans[].service`; that field never contains
  "Copilot". Match the service plan **id** against the tenant's `subscribedSkus` instead.
- Prompts are generated per-persona and reference that person's actual work (their storylines), so
  Copilot has genuine tenant content to ground its answer on.

### The open question — answered, with a caveat

**Microsoft does not document whether prompts sent through this API appear in the admin centre's
Copilot usage reports.** The reports describe usage in Teams, Outlook, Word, Excel, PowerPoint,
OneNote and the Copilot app. Whether an API-driven conversation is attributed the same way is simply
not stated.

It has now been measured. Against a CDX demo tenant on 2026-08-12, `verify-copilot` sent a marked
prompt and **found the marker in the user's interaction history**, alongside four other
interactions:

```
Prompt sent        yes
Interactions read  5
Marker found       YES
```

So API-driven prompts *are* written to the enterprise interaction store — the same store the
compliance and usage surfaces read from. That is the strongest signal available in real time.

Be careful about what it does not prove: the interaction export API and the admin centre's daily
**active users** report are related but not identical surfaces, and the latter is a next-day
aggregate. If a demo hinges on the usage report specifically, check it the following day rather than
trusting this alone. Re-run the check on any new tenant — this is a preview API and the answer is
allowed to change.

Measure it yourself with:

```pwsh
tenant-pulse verify-copilot --live --user <upn> --app-token <jwt>
```

This sends a prompt containing a unique marker, waits, then reads that user's interaction history
back through the **Copilot Interaction Export API**
(`GET /copilot/users/{id}/interactionHistory/getAllEnterpriseInteractions`) looking for the marker.
That API is application-permission only (`AiEnterpriseInteraction.Read.All`), which is why you must
supply an app-only token.

The app registration is a public client with no secret, so minting that token means adding a client
secret temporarily. Add it, take the token, and remove the secret again in the same sitting.

### What each outcome means

**Marker found.** API-driven prompts are landing in the same interaction store the compliance and
usage surfaces read from — this is what the reference tenant returned. Leave
`Copilot.UseGraphChatApi = true` and you're done.

**Marker not found.** Either the export pipeline is lagging, or API prompts are not recorded.
In order:

1. Re-run with a longer `--wait` (a few minutes).
2. Check the admin centre Copilot usage report for that user the next day — these reports are daily
   aggregates, not real time.
3. If it still hasn't registered, the API is not a usage signal. Set
   `Copilot.UseGraphChatApi = false` and drive Copilot through the browser instead (below).

### Browser fallback

If the API doesn't register as usage, the guaranteed alternative is to drive
`m365.cloud.microsoft/chat` as the user with Playwright: sign in once per user headed, persist
`storageState`, then replay prompts headless.

It genuinely registers — it *is* the product — at the cost of being slower, heavier, and dependent
on UI selectors that Microsoft changes without notice. tenant-pulse is structured so this slots in
behind the same `IActivityExecutor` interface as everything else; it is not implemented yet, because
it should only be built if verification shows it's needed.

Keep any browser automation to normal human volumes. Simulating a working day in a tenant you own
is fine; hammering Microsoft's service is not, and the terms prohibit stress testing.

---

## Copilot Studio agents

Much simpler and much more certain. Agents are driven over the **Bot Framework Direct Line API 3.0**,
which is GA, documented and stable, and agent conversations show up in Copilot Studio's own
analytics.

Configure them in `Copilot.Agents`:

```jsonc
{
  "Name": "HR Assistant",
  "DirectLineSecret": "<from the agent's Web channel security settings>",
  "Endpoint": "https://directline.botframework.com",
  "SamplePrompts": [
    "How many days of annual leave do I have left?",
    "What's the process for booking business travel?"
  ]
}
```

Then enable the workload:

```jsonc
"Workloads": { "Agents": true }
```

Notes:

- Use the **regional** endpoint if your agent is not in the default region
  (e.g. `https://europe.directline.botframework.com`).
- Each conversation is bound to the acting persona so analytics don't show one anonymous super-user.
- `SamplePrompts` are preferred most of the time, because a prompt written for the agent is far more
  likely to get a real answer than one generated blind. Leave the list empty to always generate.

---

## Declarative agents in Microsoft 365 Copilot

Declarative agents have no standalone invocation endpoint — they run inside the Copilot chat
experience and are addressed with `@AgentName`. In principle they can be reached by putting the
mention in a Copilot Chat API prompt. Whether interactions are then attributed to that agent in the
admin centre's agent reports is unverified, so tenant-pulse doesn't claim to drive them. If you want
to try, add a storyline beat whose topic starts with the mention.

---

## Tuning volume

```jsonc
"Copilot": {
  "Enabled": true,
  "PromptsPerUserPerDay": 3
}
```

Actual frequency is also shaped by each persona's `CopilotAffinity`, so an analyst reaches for
Copilot far more than a facilities manager — which is both more realistic and more useful when the
usage report is the thing being demoed.
