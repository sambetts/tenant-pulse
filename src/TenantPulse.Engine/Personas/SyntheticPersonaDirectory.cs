using TenantPulse.Core;
using TenantPulse.Core.Configuration;
using TenantPulse.Core.Personas;

namespace TenantPulse.Engine.Personas;

/// <summary>
/// A fabricated 25-person workforce shaped like a typical CDX content pack, used by
/// <c>plan --offline</c>.
/// <para>
/// This lets somebody see exactly what tenant-pulse would do — the storylines, the cast, the
/// timing, the generated wording — before they configure a tenant, register an app or enrol a
/// single user. It never touches a tenant and is never used by <c>run</c>.
/// </para>
/// </summary>
public static class SyntheticPersonaDirectory
{
    private sealed record Seed(
        string Name,
        string JobTitle,
        string Department,
        PersonaArchetype Archetype,
        string Office);

    private static readonly Seed[] Workforce =
    [
        new("Alex Wilber", "Chief Executive Officer", "Executive", PersonaArchetype.Executive, "London"),
        new("Megan Bowen", "Chief Operating Officer", "Executive", PersonaArchetype.Executive, "London"),
        new("Adele Vance", "Sales Director", "Sales", PersonaArchetype.Manager, "London"),
        new("Nestor Wilke", "Account Executive", "Sales", PersonaArchetype.Sales, "New York"),
        new("Pradeep Gupta", "Account Executive", "Sales", PersonaArchetype.Sales, "Bangalore"),
        new("Lynne Robbins", "Head of Marketing", "Marketing", PersonaArchetype.Manager, "London"),
        new("Diego Siciliani", "Product Marketing Manager", "Marketing", PersonaArchetype.Marketing, "Madrid"),
        new("Isaiah Langer", "Content Designer", "Marketing", PersonaArchetype.Marketing, "Seattle"),
        new("Patti Fernandez", "Engineering Manager", "Engineering", PersonaArchetype.Manager, "Seattle"),
        new("Miriam Graham", "Principal Engineer", "Engineering", PersonaArchetype.Engineer, "Seattle"),
        new("Johanna Lorenz", "Senior Engineer", "Engineering", PersonaArchetype.Engineer, "Berlin"),
        new("Joni Sherman", "Software Engineer", "Engineering", PersonaArchetype.Engineer, "Dublin"),
        new("Henrietta Mueller", "Solution Architect", "Engineering", PersonaArchetype.Engineer, "Berlin"),
        new("Christie Cline", "Finance Controller", "Finance", PersonaArchetype.Finance, "London"),
        new("Debra Berger", "Financial Analyst", "Finance", PersonaArchetype.Analyst, "London"),
        new("Grady Archie", "Procurement Lead", "Operations", PersonaArchetype.Operations, "Manchester"),
        new("Irvin Sayers", "Operations Manager", "Operations", PersonaArchetype.Manager, "Manchester"),
        new("Lidia Holloway", "People Director", "People", PersonaArchetype.HumanResources, "London"),
        new("Lee Gu", "People Partner", "People", PersonaArchetype.HumanResources, "Singapore"),
        new("Emily Braun", "Customer Success Manager", "Support", PersonaArchetype.Support, "Dublin"),
        new("Enrico Cattaneo", "Support Engineer", "Support", PersonaArchetype.Support, "Paris"),
        new("Alland Deyoung", "Data Analyst", "Data", PersonaArchetype.Analyst, "Amsterdam"),
        new("Cameron White", "Business Analyst", "Data", PersonaArchetype.Analyst, "Sydney"),
        new("Nathan Rigby", "Legal Counsel", "Legal", PersonaArchetype.Legal, "London"),
        new("Bianca Pisani", "IT Manager", "IT", PersonaArchetype.Engineer, "Milan")
    ];

    /// <summary>Builds the synthetic workforce using the same trait model as real personas.</summary>
    public static IReadOnlyList<Persona> Create(TenantPulseOptions options)
    {
        var domain = options.Tenant.AllowedDomains.FirstOrDefault() ?? "contoso.onmicrosoft.com";

        return
        [
            .. Workforce.Select((seed, index) =>
            {
                var rng = DeterministicRandom.For(options.Simulation.Seed, "synthetic", seed.Name);
                var startHour = rng.Chance(0.25) ? 8 : rng.Chance(0.5) ? 9 : 10;

                var local = seed.Name.Replace(' ', '.').ToLowerInvariant();

                return new Persona
                {
                    Id = $"synthetic-{index:D2}",
                    UserPrincipalName = $"{local}@{domain.TrimStart('@')}",
                    DisplayName = seed.Name,
                    GivenName = seed.Name.Split(' ')[0],
                    JobTitle = seed.JobTitle,
                    Department = seed.Department,
                    OfficeLocation = seed.Office,
                    TimeZoneId = TimeZoneFor(seed.Office, options.Simulation.DefaultTimeZone),
                    Archetype = seed.Archetype,
                    HasCopilotLicence = true,
                    Traits = TraitsFor(seed.Archetype, rng),
                    WorkingHours = new WorkingHours
                    {
                        Start = new TimeOnly(startHour, rng.Chance(0.5) ? 0 : 30),
                        End = new TimeOnly(startHour + 8, rng.Chance(0.5) ? 0 : 30),
                        LunchStart = new TimeOnly(12, rng.Chance(0.5) ? 0 : 30),
                        LunchMinutes = 30 + rng.Next(0, 4) * 15
                    }
                };
            })
        ];
    }

    private static PersonaTraits TraitsFor(PersonaArchetype archetype, Random rng)
    {
        var baseline = archetype switch
        {
            PersonaArchetype.Executive => new PersonaTraits
            {
                Chattiness = 0.45, MailVolume = 0.85, FileVolume = 0.25,
                CopilotAffinity = 0.8, Formality = 0.7, MeetingLoad = 0.9
            },
            PersonaArchetype.Manager => new PersonaTraits
            {
                Chattiness = 0.7, MailVolume = 0.75, FileVolume = 0.5,
                CopilotAffinity = 0.7, Formality = 0.55, MeetingLoad = 0.75
            },
            PersonaArchetype.Engineer => new PersonaTraits
            {
                Chattiness = 0.75, MailVolume = 0.3, FileVolume = 0.7,
                CopilotAffinity = 0.75, Formality = 0.25, MeetingLoad = 0.35
            },
            PersonaArchetype.Sales => new PersonaTraits
            {
                Chattiness = 0.8, MailVolume = 0.9, FileVolume = 0.45,
                CopilotAffinity = 0.65, Formality = 0.5, MeetingLoad = 0.7
            },
            PersonaArchetype.Marketing => new PersonaTraits
            {
                Chattiness = 0.75, MailVolume = 0.6, FileVolume = 0.7,
                CopilotAffinity = 0.8, Formality = 0.4, MeetingLoad = 0.55
            },
            PersonaArchetype.Finance => new PersonaTraits
            {
                Chattiness = 0.4, MailVolume = 0.65, FileVolume = 0.85,
                CopilotAffinity = 0.5, Formality = 0.75, MeetingLoad = 0.45
            },
            PersonaArchetype.Analyst => new PersonaTraits
            {
                Chattiness = 0.5, MailVolume = 0.45, FileVolume = 0.9,
                CopilotAffinity = 0.85, Formality = 0.5, MeetingLoad = 0.4
            },
            PersonaArchetype.Support => new PersonaTraits
            {
                Chattiness = 0.9, MailVolume = 0.6, FileVolume = 0.35,
                CopilotAffinity = 0.6, Formality = 0.35, MeetingLoad = 0.3
            },
            PersonaArchetype.HumanResources => new PersonaTraits
            {
                Chattiness = 0.55, MailVolume = 0.7, FileVolume = 0.6,
                CopilotAffinity = 0.6, Formality = 0.7, MeetingLoad = 0.6
            },
            PersonaArchetype.Legal => new PersonaTraits
            {
                Chattiness = 0.35, MailVolume = 0.7, FileVolume = 0.8,
                CopilotAffinity = 0.45, Formality = 0.9, MeetingLoad = 0.4
            },
            _ => new PersonaTraits()
        };

        return baseline with
        {
            Chattiness = Jitter(baseline.Chattiness, rng),
            MailVolume = Jitter(baseline.MailVolume, rng),
            FileVolume = Jitter(baseline.FileVolume, rng),
            CopilotAffinity = Jitter(baseline.CopilotAffinity, rng),
            TypicalReplyLatencyMinutes = 10 + rng.Next(0, 110)
        };
    }

    private static double Jitter(double value, Random rng) =>
        Math.Clamp(value + (rng.NextDouble() - 0.5) * 0.3, 0.05, 1.0);

    private static string TimeZoneFor(string office, string fallback) => office switch
    {
        "London" or "Manchester" => "Europe/London",
        "Dublin" => "Europe/Dublin",
        "Paris" => "Europe/Paris",
        "Madrid" => "Europe/Madrid",
        "Berlin" => "Europe/Berlin",
        "Milan" => "Europe/Rome",
        "Amsterdam" => "Europe/Amsterdam",
        "New York" => "America/New_York",
        "Seattle" => "America/Los_Angeles",
        "Bangalore" => "Asia/Kolkata",
        "Singapore" => "Asia/Singapore",
        "Sydney" => "Australia/Sydney",
        _ => fallback
    };
}
