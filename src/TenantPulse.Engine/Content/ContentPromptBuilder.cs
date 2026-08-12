using System.Text;
using TenantPulse.Core.Configuration;
using TenantPulse.Core.Content;
using TenantPulse.Core.Personas;

namespace TenantPulse.Engine.Content;

public sealed class ContentPromptBuilder(TenantPulseOptions options)
{
    public ContentPrompt Build(ContentRequest request)
    {
        var actor = request.Intent.Actor;
        var traits = actor.Traits;
        var system = new StringBuilder();

        system.AppendLine("You write realistic workplace content for a Microsoft 365 demo tenant.");
        system.AppendLine($"Write as {actor.FirstName}, a {ValueOrDefault(actor.JobTitle, actor.Archetype.ToString().ToLowerInvariant())} in {ValueOrDefault(actor.Department, "the business")} at {options.Content.CompanyName}, a {options.Content.CompanyIndustry} company.");
        system.AppendLine($"Persona: archetype={actor.Archetype}; formality={traits.Formality:0.00}; chattiness={traits.Chattiness:0.00}; Copilot affinity={traits.CopilotAffinity:0.00}.");
        system.AppendLine("Sound like that person at work. Address recipients by first name only.");
        system.AppendLine("Never mention AI, simulation, tenant-pulse, generated content, demo tenant, or placeholders.");
        system.AppendLine("Do not use markdown code fences, lorem ipsum, [Name], [insert date], or a Subject: prefix inside the body.");
        system.AppendLine("Return a JSON object only with this shape: {\"subject\":\"...\",\"body\":\"...\"}. Use null for subject when the shape has no subject.");

        var user = new StringBuilder();
        user.AppendLine($"Shape: {request.Shape}");
        user.AppendLine($"Topic: {request.Intent.Topic}");
        AppendIfPresent(user, "Storyline title", request.Intent.Hint("storylineTitle"));
        AppendIfPresent(user, "Storyline summary", request.Intent.Hint("storylineSummary"));
        AppendIfPresent(user, "Tone hint", request.Intent.Hint("tone"));
        AppendIfPresent(user, "Ambient/background activity", request.Intent.Hint("ambient"));
        AppendIfPresent(user, "Thread subject", request.ThreadSubject);
        AppendIfPresent(user, "Message being replied to", request.InReplyTo);
        AppendPeople(user, request.Intent.Targets);
        AppendContext(user, request.Context);
        user.AppendLine();
        user.AppendLine(ShapeInstruction(request.Shape, actor));

        return new ContentPrompt(system.ToString().Trim(), user.ToString().Trim());
    }

    private static string ShapeInstruction(ContentShape shape, Persona actor) =>
        shape switch
        {
            ContentShape.TeamsChat => "Write a Teams chat: 1-2 short sentences, informal, lowercase-ish if natural, no greeting, no sign-off, no subject. Use emoji only if this persona is chatty/informal.",
            ContentShape.TeamsChannelPost => "Write a Teams channel post: short work-update paragraph, optionally a bold lead or 2-3 bullets. Provide a short channel post title as subject.",
            ContentShape.TeamsReply => "Write a Teams reply: one or two short lines responding directly to the quoted message. No greeting, sign-off, or subject.",
            ContentShape.EmailNew => $"Write a new email: realistic subject with no Re: prefix, 2-5 short paragraphs, appropriate greeting, concrete storyline/topic detail, and sign off with {actor.FirstName}.",
            ContentShape.EmailReply => "Write an email reply: respond specifically to the quoted message, do not repeat greeting boilerplate, keep it shorter than a new email, and set subject to null.",
            ContentShape.DocumentBody => "Write a plain-text document: subject is the document title, body has several paragraphs and may include simple headings and bullet lines. No markdown fences.",
            ContentShape.MeetingInvite => "Write a meeting invite: subject is a short meeting title, body is a 1-3 line agenda.",
            ContentShape.CopilotPrompt => "Write only the prompt text the user would type into Microsoft 365 Copilot. It must be a first-person question or instruction grounded in the persona's work context. Do not answer it. Subject null.",
            ContentShape.AgentPrompt => "Write only the prompt text the user would type into a Copilot agent. It must be a first-person question or instruction grounded in the persona's work context. Do not answer it. Subject null.",
            _ => "Write realistic workplace content for this shape."
        };

    private static void AppendPeople(StringBuilder builder, IReadOnlyList<Persona> targets)
    {
        if (targets.Count == 0)
        {
            return;
        }

        builder.AppendLine($"Recipients/participants: {string.Join(", ", targets.Select(t => $"{t.FirstName} ({ValueOrDefault(t.JobTitle, t.Archetype.ToString())})"))}");
    }

    private static void AppendContext(StringBuilder builder, IReadOnlyList<string> context)
    {
        if (context.Count == 0)
        {
            return;
        }

        builder.AppendLine("Additional context:");
        foreach (var item in context.Where(static item => !string.IsNullOrWhiteSpace(item)))
        {
            builder.AppendLine($"- {item}");
        }
    }

    private static void AppendIfPresent(StringBuilder builder, string label, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            builder.AppendLine($"{label}: {value}");
        }
    }

    private static string ValueOrDefault(string? value, string fallback) =>
        string.IsNullOrWhiteSpace(value) ? fallback : value;
}

public sealed record ContentPrompt(string System, string User);
