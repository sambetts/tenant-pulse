using TenantPulse.Core;
using TenantPulse.Core.Configuration;
using TenantPulse.Core.Content;
using TenantPulse.Core.Personas;

namespace TenantPulse.Engine.Content;

public sealed class TemplateContentGenerator(TenantPulseOptions options) : IContentGenerator
{
    public Task<GeneratedContent> GenerateAsync(ContentRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var rng = DeterministicRandom.For(options.Simulation.Seed, request.Intent.Id, request.Shape.ToString(), request.Intent.Topic);
        var content = request.Shape switch
        {
            ContentShape.EmailNew => EmailNew(request, rng),
            ContentShape.EmailReply => EmailReply(request, rng),
            ContentShape.TeamsChat => TeamsChat(request, rng),
            ContentShape.TeamsChannelPost => TeamsChannelPost(request, rng),
            ContentShape.TeamsReply => TeamsReply(request, rng),
            ContentShape.DocumentBody => DocumentBody(request, rng),
            ContentShape.CopilotPrompt => CopilotPrompt(request, rng),
            ContentShape.AgentPrompt => AgentPrompt(request, rng),
            ContentShape.MeetingInvite => MeetingInvite(request, rng),
            _ => TeamsChat(request, rng)
        };

        return Task.FromResult(content with { FromTemplate = true });
    }

    private GeneratedContent EmailNew(ContentRequest request, Random rng)
    {
        var actor = request.Intent.Actor;
        var recipient = FirstRecipient(request);
        var topic = Topic(request);
        var focus = Focus(request);
        var greeting = actor.Traits.Formality > 0.7 ? $"Hi {recipient}," : $"{recipient},";
        var opener = rng.PickOrDefault(new[]
        {
            $"I wanted to share a quick update on {topic}.",
            focus is null
                ? $"A quick note on where we are with {topic}."
                : $"Following the latest work on {topic}, a quick note on {focus}.",
            $"A quick heads-up on {topic} before we get too far into the next step."
        }) ?? $"I wanted to share a quick update on {topic}.";
        var ask = rng.PickOrDefault(new[]
        {
            $"Can you review this from the {Department(actor)} angle and send any concerns?",
            "Please add anything I missed, especially around timing and owners.",
            "If this lines up with what you are seeing, I will fold it into the next update."
        }) ?? "Please add anything I missed.";

        return new GeneratedContent
        {
            Subject = rng.PickOrDefault(new[]
            {
                $"{topic} update",
                $"{topic} — next steps",
                focus is null ? $"Quick check on {topic}" : $"{topic}: {focus}"
            }),
            Body = $"{greeting}\n\n{opener} The main thing is keeping the team aligned on the next decision and making sure nothing slips between groups.\n\n{ask}\n\nThanks,\n{actor.FirstName}",
            FromTemplate = true
        };
    }

    private static GeneratedContent EmailReply(ContentRequest request, Random rng)
    {
        var actor = request.Intent.Actor;
        var topic = Topic(request);
        var line = rng.PickOrDefault(new[]
        {
            $"That works for me. On {topic}, I would keep the scope tight and call out the open dependency.",
            $"Agree with this. I can take the first pass and send back notes before the end of the day.",
            $"Thanks — the only thing I would add is a clearer owner for the follow-up."
        }) ?? $"That works for me on {topic}.";

        return new GeneratedContent
        {
            Subject = null,
            Body = $"{line}\n\n{actor.FirstName}",
            FromTemplate = true
        };
    }

    private static GeneratedContent TeamsChat(ContentRequest request, Random rng)
    {
        var recipient = FirstRecipient(request).ToLowerInvariant();
        var topic = Topic(request).ToLowerInvariant();
        var chatty = request.Intent.Actor.Traits.Chattiness > 0.65 && request.Intent.Actor.Traits.Formality < 0.45;
        var emoji = chatty && rng.Chance(0.35) ? " 👍" : string.Empty;
        var body = rng.PickOrDefault(new[]
        {
            $"{recipient} can you take a look at {topic} before 3?",
            $"thanks — pushed the update for {topic}{emoji}",
            $"quick check: are we still good with the plan for {topic}?",
            $"i added a few notes on {topic}; shout if anything looks off{emoji}"
        }) ?? $"can you take a look at {topic}?";

        return new GeneratedContent { Subject = null, Body = body, FromTemplate = true };
    }

    private static GeneratedContent TeamsChannelPost(ContentRequest request, Random rng)
    {
        var topic = Topic(request);
        var department = Department(request.Intent.Actor);
        var body = rng.PickOrDefault(new[]
        {
            $"**{topic} update**\n\nWe have the latest inputs from {department} and are tracking the remaining actions. Please add any blockers in the thread so we can close them out.",
            $"Quick update on {topic}: the core plan is in good shape, with a couple of follow-ups still open. I will keep the thread current as owners confirm timing.",
            $"For {topic}, please focus on:\n- confirming owners\n- flagging customer-facing risks\n- sharing any changes before the next checkpoint"
        }) ?? $"Quick update on {topic}: please add any blockers in the thread.";

        return new GeneratedContent
        {
            Subject = rng.PickOrDefault(new[] { $"{topic} update", $"{topic} checkpoint", $"Next steps for {topic}" }),
            Body = body,
            FromTemplate = true
        };
    }

    private static GeneratedContent TeamsReply(ContentRequest request, Random rng)
    {
        var topic = Topic(request).ToLowerInvariant();
        var body = rng.PickOrDefault(new[]
        {
            $"yes, that matches what I have for {topic}.",
            "agreed — I will update the notes.",
            "thanks, that helps. I will take the follow-up.",
            $"good call. let's keep {topic} moving with that assumption."
        }) ?? "agreed — I will update the notes.";

        return new GeneratedContent { Subject = null, Body = body, FromTemplate = true };
    }

    private GeneratedContent DocumentBody(ContentRequest request, Random rng)
    {
        var topic = Topic(request);
        var focus = Focus(request);
        var company = options.Content.CompanyName;
        var subtitle = focus is null ? topic : $"{topic}: {focus}";
        var body = rng.PickOrDefault(new[]
        {
            $"Overview\n{company} is continuing work on {topic}. The current focus is aligning owners, risks, and the next decision point.\n\nKey points\n- Confirm the immediate owner for each action\n- Keep customer-facing assumptions visible\n- Revisit timing after the next stakeholder check-in\n\nNext steps\nThe team should use this note as the working summary until the next update is available.",
            $"{subtitle}\n\nThis note captures the current state of {topic}. The work is moving, but the team still needs a shared view of dependencies and deadlines.\n\nOpen questions\n- What needs leadership review?\n- Which items can be handled within the team?\n- What should be communicated broadly?\n\nRecommendation\nKeep the next update concise and focused on decisions rather than background."
        }) ?? $"{subtitle}\n\nCurrent working notes for {topic}.";

        return new GeneratedContent
        {
            Subject = rng.PickOrDefault(new[]
            {
                $"{topic} — working notes",
                $"{topic} summary",
                focus is null ? topic : $"{topic} — {focus}"
            }),
            Body = body,
            FromTemplate = true
        };
    }

    private static GeneratedContent MeetingInvite(ContentRequest request, Random rng)
    {
        var topic = Topic(request);
        var body = rng.PickOrDefault(new[]
        {
            $"Agenda:\n1. Review current status for {topic}\n2. Confirm open actions and owners\n3. Agree next checkpoint",
            $"Walk through {topic}, resolve blockers, and confirm who owns each next step.",
            $"Short sync to align on {topic} and make sure the next update is ready."
        }) ?? $"Review {topic} and confirm next steps.";

        return new GeneratedContent
        {
            Subject = rng.PickOrDefault(new[] { $"{topic} walkthrough", $"{topic} checkpoint", $"{topic} sync" }),
            Body = body,
            FromTemplate = true
        };
    }

    private static GeneratedContent CopilotPrompt(ContentRequest request, Random rng)
    {
        var topic = Topic(request);
        var department = Department(request.Intent.Actor);
        var body = rng.PickOrDefault(new[]
        {
            $"Summarise the latest updates on {topic} and list any open actions for me.",
            $"Help me prepare for my next {department} discussion on {topic}; include risks, owners, and recent decisions.",
            $"Find the latest context I have on {topic} and draft a short update I can send to the team."
        }) ?? $"Summarise the latest updates on {topic} for me.";

        return new GeneratedContent { Subject = null, Body = body, FromTemplate = true };
    }

    private static GeneratedContent AgentPrompt(ContentRequest request, Random rng)
    {
        var topic = Topic(request);
        var body = rng.PickOrDefault(new[]
        {
            $"Check my recent context for {topic} and tell me what I should follow up on next.",
            $"Review {topic} from my work context and suggest the highest-priority next action.",
            $"Use what I have been working on around {topic} to draft a concise status update."
        }) ?? $"Check my recent context for {topic}.";

        return new GeneratedContent { Subject = null, Body = body, FromTemplate = true };
    }

    /// <summary>
    /// The thing being worked on. Ambient topics arrive as "Storyline — specific angle"; this
    /// returns just the storyline part so templates can name the work without swallowing the angle.
    /// </summary>
    private static string Topic(ContentRequest request)
    {
        var raw = request.Intent.Topic;

        if (string.IsNullOrWhiteSpace(raw))
        {
            return "the current workstream";
        }

        var separator = raw.IndexOf(" — ", StringComparison.Ordinal);
        return separator > 0 ? raw[..separator].Trim() : raw.Trim();
    }

    /// <summary>
    /// The specific angle on the work ("chasing the outstanding bits"), when the topic carries one.
    /// </summary>
    private static string? Focus(ContentRequest request)
    {
        var raw = request.Intent.Topic;

        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var separator = raw.IndexOf(" — ", StringComparison.Ordinal);
        if (separator <= 0)
        {
            return null;
        }

        var focus = raw[(separator + 3)..].Trim();
        return string.IsNullOrWhiteSpace(focus) ? null : focus;
    }

    private static string StorylineTitle(ContentRequest request) =>
        request.Intent.Hint("storylineTitle") ?? Topic(request);

    private static string FirstRecipient(ContentRequest request) =>
        request.Intent.Targets.Count == 0 ? "team" : request.Intent.Targets[0].FirstName;

    private static string Department(Persona persona) =>
        string.IsNullOrWhiteSpace(persona.Department) ? "the team" : persona.Department;
}
