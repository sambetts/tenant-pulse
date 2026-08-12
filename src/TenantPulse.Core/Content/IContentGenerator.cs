using TenantPulse.Core.Activities;

namespace TenantPulse.Core.Content;

/// <summary>What kind of text is being asked for. Shapes length, format and tone.</summary>
public enum ContentShape
{
    EmailNew,
    EmailReply,
    TeamsChat,
    TeamsChannelPost,
    TeamsReply,
    DocumentBody,
    CopilotPrompt,
    AgentPrompt,
    MeetingInvite
}

/// <summary>A request for generated content, built from an intent plus any live thread context.</summary>
public sealed record ContentRequest
{
    public required ContentShape Shape { get; init; }

    public required ActivityIntent Intent { get; init; }

    /// <summary>Quoted text being replied to, when relevant.</summary>
    public string? InReplyTo { get; init; }

    /// <summary>Subject/title of the thread being continued, when relevant.</summary>
    public string? ThreadSubject { get; init; }

    /// <summary>Extra grounding, e.g. names of related documents.</summary>
    public IReadOnlyList<string> Context { get; init; } = [];
}

/// <summary>Generated content. <see cref="Subject"/> is null for shapes that have no subject.</summary>
public sealed record GeneratedContent
{
    public string? Subject { get; init; }

    public required string Body { get; init; }

    /// <summary>True when this came from the template fallback rather than the LLM.</summary>
    public bool FromTemplate { get; init; }
}

/// <summary>Produces the words that make simulated activity look like real work.</summary>
public interface IContentGenerator
{
    Task<GeneratedContent> GenerateAsync(ContentRequest request, CancellationToken cancellationToken);
}
