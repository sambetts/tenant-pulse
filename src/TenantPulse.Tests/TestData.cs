using TenantPulse.Core.Personas;

namespace TenantPulse.Tests;

/// <summary>Builders for test personas and storylines, so tests stay readable.</summary>
internal static class TestData
{
    public static Persona Persona(
        string id,
        string name,
        PersonaArchetype archetype = PersonaArchetype.Operations,
        string department = "Operations",
        string timeZone = "Europe/London",
        bool copilot = true) => new()
        {
            Id = id,
            UserPrincipalName = $"{name.Replace(" ", ".").ToLowerInvariant()}@contoso.onmicrosoft.com",
            DisplayName = name,
            GivenName = name.Split(' ')[0],
            JobTitle = archetype.ToString(),
            Department = department,
            TimeZoneId = timeZone,
            Archetype = archetype,
            HasCopilotLicence = copilot,
            WorkingHours = new WorkingHours
            {
                Start = new TimeOnly(9, 0),
                End = new TimeOnly(17, 30),
                LunchStart = new TimeOnly(12, 30),
                LunchMinutes = 45
            }
        };

    public static IReadOnlyList<Persona> Workforce(int count = 8)
    {
        PersonaArchetype[] archetypes =
        [
            PersonaArchetype.Executive, PersonaArchetype.Manager, PersonaArchetype.Engineer,
            PersonaArchetype.Sales, PersonaArchetype.Marketing, PersonaArchetype.Finance,
            PersonaArchetype.HumanResources, PersonaArchetype.Support, PersonaArchetype.Analyst,
            PersonaArchetype.Operations
        ];

        string[] departments =
        [
            "Executive", "Engineering", "Sales", "Marketing", "Finance",
            "People", "Support", "Operations"
        ];

        return [.. Enumerable.Range(0, count).Select(i => Persona(
            id: $"user-{i:D2}",
            name: $"Person{i:D2} Example",
            archetype: archetypes[i % archetypes.Length],
            department: departments[i % departments.Length]))];
    }
}
