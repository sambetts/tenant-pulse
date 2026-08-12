namespace TenantPulse.Core.Configuration;

/// <summary>
/// Root configuration. Bound from config/tenant-pulse.json (gitignored) plus environment variables.
/// </summary>
public sealed class TenantPulseOptions
{
    public const string SectionName = "TenantPulse";

    public TenantOptions Tenant { get; set; } = new();

    public AuthOptions Auth { get; set; } = new();

    public SimulationOptions Simulation { get; set; } = new();

    public LimitsOptions Limits { get; set; } = new();

    public ContentOptions Content { get; set; } = new();

    public CopilotOptions Copilot { get; set; } = new();

    public WorkloadOptions Workloads { get; set; } = new();
}

public sealed class TenantOptions
{
    /// <summary>The tenant tenant-pulse will act against.</summary>
    public string TenantId { get; set; } = string.Empty;

    /// <summary>
    /// Explicit allow-list of tenant ids this installation may touch. The safety governor refuses
    /// to run unless <see cref="TenantId"/> appears here — this is the guardrail that stops
    /// tenant-pulse ever being pointed at a production tenant by accident.
    /// </summary>
    public List<string> AllowedTenantIds { get; set; } = [];

    /// <summary>
    /// Optional allow-list of accepted domains (e.g. "contoso.onmicrosoft.com"). When set, every
    /// persona UPN must match one of these suffixes or it is excluded.
    /// </summary>
    public List<string> AllowedDomains { get; set; } = [];

    /// <summary>Entra app registration (public client) used for delegated user tokens.</summary>
    public string ClientId { get; set; } = string.Empty;

    public string Instance { get; set; } = "https://login.microsoftonline.com";

    public string GraphBaseUrl { get; set; } = "https://graph.microsoft.com";

    public string Authority => $"{Instance.TrimEnd('/')}/{TenantId}";
}

public enum AuthMode
{
    /// <summary>Interactive one-off device-code enrolment per user, then silent refresh. Recommended.</summary>
    DeviceCode,

    /// <summary>
    /// Username/password (ROPC). Deprecated by Microsoft and blocked by MFA/security defaults, but
    /// usually works in a demo tenant and makes enrolling 25 users a single unattended command.
    /// </summary>
    UsernamePassword
}

public sealed class AuthOptions
{
    public AuthMode Mode { get; set; } = AuthMode.DeviceCode;

    /// <summary>Directory holding the encrypted per-user MSAL token caches.</summary>
    public string CacheDirectory { get; set; } = ".state/token-cache";

    /// <summary>
    /// For <see cref="AuthMode.UsernamePassword"/> only: a shared password used by all demo users
    /// (CDX provisions them this way). Read from configuration or the TENANTPULSE_SHARED_PASSWORD
    /// environment variable — never commit it.
    /// </summary>
    public string? SharedPassword { get; set; }

    /// <summary>Per-user password overrides (UPN → password) for tenants that don't share one.</summary>
    public Dictionary<string, string> Passwords { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Delegated scopes requested for general activity.
    /// <para>
    /// The last block is required by the Graph Copilot Chat API, which grounds answers in the
    /// user's own content and so demands read access to each source. It rejects a token that is
    /// missing any of them with HTTP 403 listing the lot, even where a broader scope (for example
    /// Mail.ReadWrite over Mail.Read) is already present — the literal scope has to be in the token.
    /// </para>
    /// </summary>
    public List<string> Scopes { get; set; } =
    [
        "User.Read",
        "User.ReadBasic.All",
        "User.Read.All",
        "Organization.Read.All",
        "Mail.ReadWrite",
        "Mail.Send",
        "Chat.ReadWrite",
        "ChannelMessage.Send",
        "ChannelMessage.Read.All",
        "Team.ReadBasic.All",
        "Channel.ReadBasic.All",
        "Files.ReadWrite.All",
        "Sites.ReadWrite.All",
        "Calendars.ReadWrite",

        // Required by the Copilot Chat API.
        "Mail.Read",
        "Chat.Read",
        "Sites.Read.All",
        "People.Read.All",
        "ExternalItem.Read.All",
        "OnlineMeetingTranscript.Read.All"
    ];
}

public sealed class SimulationOptions
{
    /// <summary>Deterministic seed. The same seed replays the same simulation.</summary>
    public int Seed { get; set; } = 20260812;

    /// <summary>
    /// When true nothing is written to the tenant — the plan is produced and logged only.
    /// Defaults to true so an unconfigured run can never modify a tenant.
    /// </summary>
    public bool DryRun { get; set; } = true;

    /// <summary>Fallback IANA time zone for personas with no usable mailbox time zone.</summary>
    public string DefaultTimeZone { get; set; } = "Europe/London";

    /// <summary>How many storylines should be running at once.</summary>
    public int ConcurrentStorylines { get; set; } = 3;

    /// <summary>Simulate a little weekend/evening activity.</summary>
    public bool IncludeAfterHours { get; set; } = true;

    /// <summary>Path to the SQLite activity journal.</summary>
    public string JournalPath { get; set; } = ".state/journal.db";

    /// <summary>
    /// Optional durable copy of the journal. When set, the journal is restored from here at startup
    /// and snapshotted back to it as activity is recorded.
    /// <para>
    /// This exists for container hosting. SQLite cannot run on an SMB file share — the byte-range
    /// locking it needs is unsupported and every statement fails with "database is locked" — so the
    /// live journal goes on fast local disk and the durable copy on the mounted share. Without it,
    /// a restart would lose the purge paths and the tenant could never be cleaned up.
    /// </para>
    /// </summary>
    public string? JournalSnapshotPath { get; set; }

    /// <summary>Minimum seconds between journal snapshots. Snapshots on shutdown ignore this.</summary>
    public int JournalSnapshotIntervalSeconds { get; set; } = 60;

    /// <summary>
    /// Presence of this file stops the simulator at the next check. Deleting it resumes.
    /// </summary>
    public string KillSwitchFile { get; set; } = ".state/STOP";

    /// <summary>
    /// Header stamped on generated email so simulated content can always be identified and purged.
    /// </summary>
    public string MarkerHeaderName { get; set; } = "X-TenantPulse";

    public string MarkerValue { get; set; } = "simulated";
}

public sealed class LimitsOptions
{
    /// <summary>Hard ceiling on activities per persona per day.</summary>
    public int MaxActivitiesPerUserPerDay { get; set; } = 14;

    /// <summary>Hard ceiling across the whole tenant per hour. Keeps well below any throttling.</summary>
    public int MaxActivitiesPerTenantPerHour { get; set; } = 60;

    /// <summary>Minimum gap between two activities by the same persona.</summary>
    public int MinSecondsBetweenUserActivities { get; set; } = 90;

    /// <summary>Maximum Graph calls in flight at once.</summary>
    public int MaxConcurrency { get; set; } = 4;
}

public enum ContentProvider
{
    /// <summary>Azure OpenAI chat completions. Realistic, persona-aware prose.</summary>
    AzureOpenAI,

    /// <summary>No LLM: deterministic templates. Cheaper, obviously less varied.</summary>
    Template
}

public sealed class ContentOptions
{
    public ContentProvider Provider { get; set; } = ContentProvider.AzureOpenAI;

    /// <summary>e.g. https://my-aoai.openai.azure.com/</summary>
    public string? Endpoint { get; set; }

    /// <summary>Chat completion deployment name.</summary>
    public string? Deployment { get; set; }

    /// <summary>
    /// API key. Prefer the TENANTPULSE_AOAI_KEY environment variable; leave null to use
    /// Entra (DefaultAzureCredential-style) auth is not supported — a key is required.
    /// </summary>
    public string? ApiKey { get; set; }

    /// <summary>Fictional company the personas work for. Steers all generated content.</summary>
    public string CompanyName { get; set; } = "Contoso";

    public string CompanyIndustry { get; set; } = "professional services";

    /// <summary>Fall back to templates when the LLM errors, rather than failing the activity.</summary>
    public bool FallbackToTemplates { get; set; } = true;

    public int MaxTokens { get; set; } = 700;

    public double Temperature { get; set; } = 0.9;
}

public sealed class CopilotOptions
{
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Graph beta Copilot Chat API. Preview at time of writing; whether prompts land in the admin
    /// centre usage reports is unverified by Microsoft docs — use the <c>verify</c> command.
    /// </summary>
    public bool UseGraphChatApi { get; set; } = true;

    /// <summary>Average Copilot prompts per licensed persona per working day.</summary>
    public double PromptsPerUserPerDay { get; set; } = 3;

    /// <summary>Copilot Studio agents to talk to via Direct Line.</summary>
    public List<AgentOptions> Agents { get; set; } = [];
}

public sealed class AgentOptions
{
    public string Name { get; set; } = string.Empty;

    /// <summary>Direct Line secret from the agent's Web channel security settings.</summary>
    public string? DirectLineSecret { get; set; }

    /// <summary>Regional Direct Line endpoint.</summary>
    public string Endpoint { get; set; } = "https://directline.botframework.com";

    /// <summary>Example prompts this agent is good at answering.</summary>
    public List<string> SamplePrompts { get; set; } = [];
}

public sealed class WorkloadOptions
{
    public bool Mail { get; set; } = true;

    public bool Teams { get; set; } = true;

    public bool Files { get; set; } = true;

    public bool Calendar { get; set; } = true;

    public bool Copilot { get; set; } = true;

    public bool Agents { get; set; }

    /// <summary>SharePoint sites (by URL path, e.g. "/sites/Marketing") eligible for file activity.</summary>
    public List<string> SharePointSites { get; set; } = [];

    /// <summary>When empty, all teams the actor belongs to are eligible.</summary>
    public List<string> TeamNames { get; set; } = [];
}
