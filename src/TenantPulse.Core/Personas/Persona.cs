namespace TenantPulse.Core.Personas;

/// <summary>
/// Broad behavioural category for a simulated user. Drives which storyline roles a persona can
/// take, how much they communicate, and how likely they are to reach for Copilot.
/// </summary>
public enum PersonaArchetype
{
    Executive,
    Manager,
    Engineer,
    Sales,
    Marketing,
    Finance,
    HumanResources,
    Operations,
    Support,
    Analyst,
    Legal
}

/// <summary>
/// Per-persona behavioural dials. All 0..1 scalars unless stated otherwise.
/// </summary>
public sealed record PersonaTraits
{
    /// <summary>How much this persona talks in Teams relative to peers.</summary>
    public double Chattiness { get; init; } = 0.5;

    /// <summary>How much email this persona sends relative to peers.</summary>
    public double MailVolume { get; init; } = 0.5;

    /// <summary>How often this persona creates or edits documents.</summary>
    public double FileVolume { get; init; } = 0.5;

    /// <summary>How readily this persona uses Copilot and Copilot agents.</summary>
    public double CopilotAffinity { get; init; } = 0.5;

    /// <summary>0 = very casual ("yep, on it"), 1 = very formal ("Dear colleagues,").</summary>
    public double Formality { get; init; } = 0.5;

    /// <summary>How much of the day is consumed by meetings.</summary>
    public double MeetingLoad { get; init; } = 0.4;

    /// <summary>Typical minutes before this persona replies to something addressed to them.</summary>
    public int TypicalReplyLatencyMinutes { get; init; } = 45;

    /// <summary>Probability the persona does a little work outside their working hours.</summary>
    public double AfterHoursPropensity { get; init; } = 0.1;
}

/// <summary>
/// The working pattern for a persona, expressed in their own local time zone.
/// </summary>
public sealed record WorkingHours
{
    public TimeOnly Start { get; init; } = new(9, 0);
    public TimeOnly End { get; init; } = new(17, 30);
    public TimeOnly LunchStart { get; init; } = new(12, 30);
    public int LunchMinutes { get; init; } = 45;

    /// <summary>Days this persona works. Defaults to Mon–Fri.</summary>
    public IReadOnlySet<DayOfWeek> WorkingDays { get; init; } = new HashSet<DayOfWeek>
    {
        DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday
    };

    public bool IsWorkingDay(DateOnly date) => WorkingDays.Contains(date.DayOfWeek);

    /// <summary>True when <paramref name="local"/> falls inside the working window, excluding lunch.</summary>
    public bool IsWithinWorkingHours(DateTime local)
    {
        if (!IsWorkingDay(DateOnly.FromDateTime(local)))
        {
            return false;
        }

        var t = TimeOnly.FromDateTime(local);
        if (t < Start || t > End)
        {
            return false;
        }

        var lunchEnd = LunchStart.AddMinutes(LunchMinutes);
        return t < LunchStart || t >= lunchEnd;
    }
}

/// <summary>
/// A simulated user: a real, licensed account in the demo tenant plus the behavioural model
/// tenant-pulse uses to decide what that account does and when.
/// </summary>
public sealed record Persona
{
    /// <summary>Entra object id of the real account.</summary>
    public required string Id { get; init; }

    public required string UserPrincipalName { get; init; }

    public required string DisplayName { get; init; }

    public string? GivenName { get; init; }

    public string? JobTitle { get; init; }

    public string? Department { get; init; }

    public string? OfficeLocation { get; init; }

    /// <summary>UPN of this persona's manager, when the tenant models one.</summary>
    public string? ManagerUpn { get; init; }

    /// <summary>IANA time zone id, e.g. "Europe/London".</summary>
    public string TimeZoneId { get; init; } = "Europe/London";

    public PersonaArchetype Archetype { get; init; } = PersonaArchetype.Operations;

    public PersonaTraits Traits { get; init; } = new();

    public WorkingHours WorkingHours { get; init; } = new();

    /// <summary>True when the account holds a Microsoft 365 Copilot licence.</summary>
    public bool HasCopilotLicence { get; init; }

    /// <summary>Excluded personas are never used as an actor (e.g. break-glass admin accounts).</summary>
    public bool Excluded { get; init; }

    public string FirstName => GivenName ?? DisplayName.Split(' ')[0];

    public TimeZoneInfo ResolveTimeZone()
    {
        if (TimeZoneInfo.TryFindSystemTimeZoneById(TimeZoneId, out var tz))
        {
            return tz;
        }

        return TimeZoneInfo.Utc;
    }
}
