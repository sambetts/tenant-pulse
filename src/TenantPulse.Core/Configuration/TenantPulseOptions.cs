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

    public AdminOptions Admin { get; set; } = new();
}

/// <summary>
/// The admin web hosted inside <c>run</c>.
/// <para>
/// It lives in that process because the simulator is single-writer: a separate service able to
/// trigger activity would double-post into the tenant and race the journal. There is no
/// authentication here by design — the hosted deployment puts Container Apps' built-in Entra
/// authentication in front of it, which is a far better answer for a control plane that can write
/// to a tenant than anything this would hand-roll.
/// </para>
/// </summary>
public sealed class AdminOptions
{
    /// <summary>
    /// Off by default. A local <c>run</c> should not open a port nobody asked for, and the hosted
    /// deployment turns it on explicitly once authentication is in front of it.
    /// </summary>
    public bool Enabled { get; set; }

    public int Port { get; set; } = 8080;
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

    /// <summary>
    /// UPNs that join the simulated workforce even though they look like admin or service accounts.
    /// <para>
    /// Accounts whose name contains admin, svc, service, sync, breakglass or noreply are excluded by
    /// default: their activity looks wrong in reports, and one of them is usually the account the
    /// operator is signed in with. That default is right until you demo <em>as</em> one of them, at
    /// which point an empty mailbox is exactly the problem this tool exists to solve.
    /// </para>
    /// <para>
    /// This does not override <see cref="AllowedDomains"/>, which is a safety boundary rather than
    /// a tidiness one.
    /// </para>
    /// </summary>
    public List<string> AlwaysIncludeUsers { get; set; } = [];

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

    /// <summary>
    /// Volume dial for ambient activity: 1.0 is the default hum, 2.0 is roughly twice as much.
    /// <para>
    /// It multiplies each persona's trait-derived budget rather than replacing it, so the quiet
    /// people stay quieter than the chatty ones at every setting. Storyline beats are deliberately
    /// unaffected — they are scripted to be coherent, and duplicating them would read as a stutter
    /// rather than as a busier business.
    /// </para>
    /// <para>
    /// <see cref="LimitsOptions.MaxActivitiesPerUserPerDay"/> and
    /// <see cref="LimitsOptions.MaxActivitiesPerTenantPerHour"/> still apply. Raise them alongside
    /// this or they will silently cap the increase and show up as skipped activity.
    /// </para>
    /// </summary>
    public double ActivityIntensity { get; set; } = 1.0;

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
    /// When configured, the journal is kept in Azure Table Storage instead of SQLite.
    /// <para>
    /// This is what makes a hosted run observable: a table can be read from anywhere, so
    /// <c>report</c> and <c>purge</c> work from a laptop against a simulator running in Azure,
    /// rather than only from wherever the database file happens to sit.
    /// </para>
    /// </summary>
    public JournalTableOptions JournalTable { get; set; } = new();

    /// <summary>
    /// Presence of this file stops the simulator at the next check. Deleting it resumes.
    /// </summary>
    public string KillSwitchFile { get; set; } = ".state/STOP";

    /// <summary>
    /// Emit one line of machine-readable JSON per activity to stdout, for log-based reporting.
    /// <para>
    /// This is what makes a hosted run observable. The Table journal is the durable record but sits
    /// behind a private endpoint, whereas container stdout flows into Log Analytics and can be
    /// queried from a browser anywhere. On by default: the cost is one extra log line per activity,
    /// and the alternative is having no way to see what the simulator did.
    /// </para>
    /// </summary>
    public bool EmitActivityEvents { get; set; } = true;

    /// <summary>
    /// Header stamped on generated email so simulated content can always be identified and purged.
    /// </summary>
    public string MarkerHeaderName { get; set; } = "X-TenantPulse";

    public string MarkerValue { get; set; } = "simulated";
}

public sealed class JournalTableOptions
{
    /// <summary>
    /// Storage connection string. Covers the emulator — <c>UseDevelopmentStorage=true</c> — and
    /// local runs. Prefer <see cref="Endpoint"/> in Azure: shared-key access to storage is commonly
    /// disabled by policy.
    /// </summary>
    public string? ConnectionString { get; set; }

    /// <summary>
    /// Table endpoint, for example <c>https://mystorage.table.core.windows.net</c>. Authenticated
    /// with Entra: the managed identity when hosted, whoever is signed in to the Azure CLI locally.
    /// </summary>
    public string? Endpoint { get; set; }

    public string TableName { get; set; } = "TenantPulseJournal";

    /// <summary>True when enough has been configured to use a table at all.</summary>
    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(ConnectionString) || !string.IsNullOrWhiteSpace(Endpoint);
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
    /// API key. Prefer the TENANTPULSE_AOAI_KEY environment variable. Leave null to authenticate
    /// with Entra instead — see <see cref="UseEntraAuth"/>.
    /// </summary>
    public string? ApiKey { get; set; }

    /// <summary>
    /// Authenticate to Azure OpenAI with Entra (the managed identity when hosted, whoever is signed
    /// in to the Azure CLI locally) rather than an API key.
    /// <para>
    /// Governed subscriptions routinely set <c>disableLocalAuth</c> on the Azure OpenAI resource, at
    /// which point a key is not merely discouraged but rejected outright with
    /// <c>403 AuthenticationTypeDisabled</c> — and because content generation falls back to
    /// templates, the only symptom is blander text. Entra auth needs the
    /// <c>Cognitive Services OpenAI User</c> role on the resource.
    /// </para>
    /// <para>
    /// Entra is used automatically when no key is configured; set this to force it even when one is.
    /// </para>
    /// </summary>
    public bool UseEntraAuth { get; set; }

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
