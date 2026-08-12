# Writing storylines

Storylines are what stop a simulated tenant looking like a random noise generator. They're plain
JSON in `config/storylines.json`, so you can give a tenant an industry-appropriate narrative without
touching the code.

## The idea

A storyline is a multi-day thread of business activity with a **cast** and a sequence of **beats**.
Each beat is one activity by one role, on one day, about one thing. Because the same people work on
the same named thing across email, Teams, documents and meetings, the tenant reads as a company
doing work — and Copilot can actually answer questions about it.

## Shape

```jsonc
{
  "id": "contoso-rfp",                    // stable, unique
  "title": "Northwind Traders RFP response",
  "summary": "Background handed to the content generator so every message stays on-message.",
  "weight": 1.4,                          // relative likelihood of being chosen
  "roles": [
    {
      "name": "bidLead",
      "preferredArchetypes": ["Sales", "Manager"],   // casting preference, not a requirement
      "preferredDepartment": "Sales"                 // optional
    }
  ],
  "beats": [
    {
      "id": "kickoff-mail",
      "dayOffset": 0,                     // days from the storyline's start
      "kind": "SendMail",
      "actorRole": "bidLead",
      "targetRoles": ["pricingAnalyst"],
      "topic": "Northwind Traders RFP — kickoff and deadlines",
      "preferredHour": 9,                 // optional, actor's local time
      "hints": { "tone": "energetic but organised" }
    }
  ]
}
```

### Activity kinds

`SendMail`, `ReplyMail`, `ReadMail`, `ChatMessage`, `ChannelPost`, `ChannelReply`, `Reaction`,
`CreateDocument`, `EditDocument`, `CreateEvent`, `CopilotPrompt`, `AgentPrompt`.

### Archetypes

`Executive`, `Manager`, `Engineer`, `Sales`, `Marketing`, `Finance`, `HumanResources`, `Operations`,
`Support`, `Analyst`, `Legal`.

Archetypes are inferred from each user's real job title and department, so a well-populated demo
tenant casts itself sensibly.

### Placeholders

`{roleName}` in a topic is replaced with that cast member's first name:

```jsonc
"topic": "Catching up with {analyst} on the pricing model"
```

## What makes a good storyline

**Span several workloads.** A storyline that is only email teaches Copilot nothing and leaves Teams
empty. Mix mail, chat, a document, a meeting and a Copilot prompt.

**Give it an arc.** Kickoff → work → friction → decision → close. The friction beat (a question, a
risk, a chase) is what makes it read as real rather than as a press release.

**Name things consistently.** Reuse the same customer, project and document names across beats. This
is exactly what makes Copilot look good when someone asks about them.

**Keep it 4–8 days.** Shorter feels thin; longer and the cast drifts out of memory.

**Write topics as briefs, not subject lines.** The topic is instructions to the content generator,
not the literal text. "Northwind pricing — margin assumptions are too thin" produces better output
than "Pricing".

## Casting

Roles are matched to personas once per storyline instance, deterministically for a given seed, so
the same people stay involved throughout. Casting prefers matching archetype and department but will
fall back to anyone available. A storyline is skipped entirely if there aren't enough distinct
people to fill every role — so don't write a 10-role storyline for a 25-person tenant with 6 usable
personas.

## Scheduling

`Simulation.ConcurrentStorylines` (default 3) storylines run at once. Each "slot" runs one storyline
after another, with staggered start dates and short gaps, so they overlap rather than marching in
lockstep. When one finishes, the next is chosen by `weight`.

Beats only fire on a working day for their actor — a Monday-morning beat won't land on a bank
holiday Monday for a persona who isn't working.

## Testing your storyline

```pwsh
dotnet run --project src/TenantPulse.Cli -- plan --offline --days 7
```

That plays your catalogue against a synthetic 25-person company and shows exactly which beats fire,
when, and who they landed on — with no tenant involved. Add `--sample-content 6` to see the wording.
