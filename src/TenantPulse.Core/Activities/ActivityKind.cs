namespace TenantPulse.Core.Activities;

/// <summary>
/// Every kind of activity tenant-pulse knows how to simulate. Each value maps to exactly one
/// <see cref="IActivityExecutor"/> implementation.
/// </summary>
public enum ActivityKind
{
    /// <summary>Send a new email to one or more colleagues.</summary>
    SendMail,

    /// <summary>Reply to the most recent relevant message in the actor's inbox.</summary>
    ReplyMail,

    /// <summary>Open/read (and occasionally flag) unread mail, so mailboxes don't stay at 400 unread.</summary>
    ReadMail,

    /// <summary>Post a message into an existing 1:1 or group chat.</summary>
    ChatMessage,

    /// <summary>Post a new message into a Teams channel.</summary>
    ChannelPost,

    /// <summary>Reply in an existing Teams channel thread.</summary>
    ChannelReply,

    /// <summary>React (👍 etc.) to somebody else's recent Teams message.</summary>
    Reaction,

    /// <summary>Create a new document in OneDrive or a SharePoint document library.</summary>
    CreateDocument,

    /// <summary>Append a revision to an existing document, producing a new version.</summary>
    EditDocument,

    /// <summary>Create a calendar meeting and invite colleagues.</summary>
    CreateEvent,

    /// <summary>Send a prompt to Microsoft 365 Copilot as the actor.</summary>
    CopilotPrompt,

    /// <summary>Send a prompt to a Copilot Studio agent as the actor.</summary>
    AgentPrompt
}

/// <summary>
/// Workload groupings used for configuration, rate limiting and reporting.
/// </summary>
public enum Workload
{
    Mail,
    Teams,
    Files,
    Calendar,
    Copilot,
    Agents
}

public static class ActivityKindExtensions
{
    public static Workload ToWorkload(this ActivityKind kind) => kind switch
    {
        ActivityKind.SendMail or ActivityKind.ReplyMail or ActivityKind.ReadMail => Workload.Mail,
        ActivityKind.ChatMessage or ActivityKind.ChannelPost or ActivityKind.ChannelReply or ActivityKind.Reaction => Workload.Teams,
        ActivityKind.CreateDocument or ActivityKind.EditDocument => Workload.Files,
        ActivityKind.CreateEvent => Workload.Calendar,
        ActivityKind.CopilotPrompt => Workload.Copilot,
        ActivityKind.AgentPrompt => Workload.Agents,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unmapped activity kind.")
    };

    /// <summary>
    /// True when the activity requires the actor to hold a Microsoft 365 Copilot licence.
    /// </summary>
    public static bool RequiresCopilotLicence(this ActivityKind kind) =>
        kind is ActivityKind.CopilotPrompt;
}
