using System.Text.Json;
using Microsoft.Extensions.Logging;
using TenantPulse.Core;
using TenantPulse.Core.Configuration;
using TenantPulse.Core.Personas;
using TenantPulse.Engine.Graph;

namespace TenantPulse.Engine.Personas;

/// <summary>
/// Builds the cast list from the tenant's real, licensed users.
/// <para>
/// Nothing here is invented: job title, department and manager come from the directory, so a CDX
/// content pack's carefully-built org chart drives who talks to whom. Behavioural traits are then
/// derived deterministically from those attributes, which keeps a given user behaving consistently
/// across restarts without needing to store anything.
/// </para>
/// </summary>
public sealed class GraphPersonaDirectory(
    IGraphClient graph,
    TenantPulseOptions options,
    ILogger<GraphPersonaDirectory> logger)
{
    /// <summary>
    /// Loads personas using <paramref name="signedInUpn"/>'s token to read the directory (any
    /// enrolled user can read the user list with User.ReadBasic.All).
    /// </summary>
    public async Task<IReadOnlyList<Persona>> LoadAsync(string signedInUpn, CancellationToken cancellationToken)
    {
        const string path =
            "users?$select=id,userPrincipalName,displayName,givenName,jobTitle,department," +
            "officeLocation,accountEnabled,userType,mail&$top=200";

        var users = await graph.GetPagedAsync(signedInUpn, path, maxItems: 500, cancellationToken)
            .ConfigureAwait(false);

        var licences = await LoadLicenceAssignmentsAsync(signedInUpn, cancellationToken).ConfigureAwait(false);

        var personas = new List<Persona>();

        foreach (var user in users)
        {
            var upn = user.GetStringOrNull("userPrincipalName");
            var id = user.GetStringOrNull("id");
            var displayName = user.GetStringOrNull("displayName");

            if (string.IsNullOrWhiteSpace(upn) || string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(displayName))
            {
                continue;
            }

            if (user.TryGetProperty("accountEnabled", out var enabled) &&
                enabled.ValueKind == JsonValueKind.False)
            {
                continue;
            }

            // Guests aren't part of the organisation's day-to-day rhythm.
            if (string.Equals(user.GetStringOrNull("userType"), "Guest", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // No mailbox means no mail, and usually means a service or room account.
            if (string.IsNullOrWhiteSpace(user.GetStringOrNull("mail")))
            {
                continue;
            }

            // Room and equipment mailboxes, bots and other service accounts have a mailbox but no
            // licence. They must not join the cast: a conference room does not send email, and an
            // unlicensed account gets HTTP 403 from every workload endpoint anyway.
            if (licences.Known && !licences.Licensed.Contains(id))
            {
                continue;
            }

            var persona = BuildPersona(
                id, upn, displayName,
                user.GetStringOrNull("givenName"),
                user.GetStringOrNull("jobTitle"),
                user.GetStringOrNull("department"),
                user.GetStringOrNull("officeLocation"),
                hasCopilot: licences.Copilot.Contains(id));

            personas.Add(persona with { Excluded = ShouldExclude(persona) });
        }

        logger.LogInformation(
            "Loaded {Count} personas ({Active} active, {Copilot} Copilot-licensed).",
            personas.Count,
            personas.Count(p => !p.Excluded),
            personas.Count(p => p.HasCopilotLicence));

        return personas;
    }

    /// <summary>
    /// Licence assignments, read once for the whole tenant.
    /// </summary>
    /// <param name="Known">
    /// False when licences could not be read at all (the app registration predates the
    /// User.Read.All/Organization.Read.All scopes). Callers must then fall back to including every
    /// mailbox-bearing account rather than silently simulating nobody.
    /// </param>
    /// <param name="Licensed">Object ids of users holding at least one licence.</param>
    /// <param name="Copilot">Object ids of users holding an enabled Microsoft 365 Copilot plan.</param>
    private sealed record LicenceAssignments(
        bool Known,
        HashSet<string> Licensed,
        HashSet<string> Copilot);

    /// <summary>
    /// Reads who is licensed, and who specifically holds Microsoft 365 Copilot.
    /// <para>
    /// Copilot cannot be detected from <c>assignedPlans[].service</c>: that field carries coarse
    /// service names ("exchange", "TeamspaceAPI", "ccibotsprod") and never contains "Copilot", even
    /// for a fully licensed user. The reliable signal is the service plan <em>id</em>, matched
    /// against the Copilot service plans the tenant actually subscribes to.
    /// </para>
    /// </summary>
    private async Task<LicenceAssignments> LoadLicenceAssignmentsAsync(
        string signedInUpn,
        CancellationToken cancellationToken)
    {
        var licensed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var copilot = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var copilotPlanIds = await LoadCopilotServicePlanIdsAsync(signedInUpn, cancellationToken)
            .ConfigureAwait(false);

        try
        {
            var users = await graph
                .GetPagedAsync(
                    signedInUpn,
                    "users?$select=id,assignedLicenses,assignedPlans&$top=200",
                    500,
                    cancellationToken)
                .ConfigureAwait(false);

            foreach (var user in users)
            {
                var id = user.GetStringOrNull("id");
                if (string.IsNullOrWhiteSpace(id))
                {
                    continue;
                }

                if (user.TryGetProperty("assignedLicenses", out var assigned) &&
                    assigned.ValueKind == JsonValueKind.Array &&
                    assigned.GetArrayLength() > 0)
                {
                    licensed.Add(id);
                }

                if (!user.TryGetProperty("assignedPlans", out var plans) || plans.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                foreach (var plan in plans.EnumerateArray())
                {
                    if (!string.Equals(
                            plan.GetStringOrNull("capabilityStatus"),
                            "Enabled",
                            StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var planId = plan.GetStringOrNull("servicePlanId");

                    if (planId is not null && copilotPlanIds.Contains(planId))
                    {
                        copilot.Add(id);
                        break;
                    }
                }
            }

            return new LicenceAssignments(Known: true, licensed, copilot);
        }
        catch (GraphException ex)
        {
            logger.LogWarning(
                "Could not read licence assignments ({Status}): {Message}. Every mailbox will be " +
                "treated as a person and no Copilot activity will be planned. Re-run " +
                "scripts/setup-app-registration.ps1 to grant User.Read.All and Organization.Read.All.",
                ex.StatusCode, ex.Message);

            return new LicenceAssignments(Known: false, licensed, copilot);
        }
    }

    /// <summary>
    /// Service plan ids belonging to the tenant's Microsoft 365 Copilot subscriptions.
    /// </summary>
    private async Task<HashSet<string>> LoadCopilotServicePlanIdsAsync(
        string signedInUpn,
        CancellationToken cancellationToken)
    {
        var planIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            var skus = await graph
                .GetPagedAsync(signedInUpn, "subscribedSkus", 200, cancellationToken)
                .ConfigureAwait(false);

            foreach (var sku in skus)
            {
                if (!sku.TryGetProperty("servicePlans", out var servicePlans) ||
                    servicePlans.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                foreach (var servicePlan in servicePlans.EnumerateArray())
                {
                    var name = servicePlan.GetStringOrNull("servicePlanName");
                    var id = servicePlan.GetStringOrNull("servicePlanId");

                    if (id is not null &&
                        name is not null &&
                        name.Contains("COPILOT", StringComparison.OrdinalIgnoreCase))
                    {
                        planIds.Add(id);
                    }
                }
            }
        }
        catch (GraphException ex)
        {
            logger.LogWarning(
                "Could not read subscribed SKUs ({Status}); assuming no Copilot licences. {Message}",
                ex.StatusCode, ex.Message);
        }

        return planIds;
    }

    private bool ShouldExclude(Persona persona)
    {
        if (options.Tenant.AllowedDomains.Count > 0 &&
            !options.Tenant.AllowedDomains.Any(d =>
                persona.UserPrincipalName.EndsWith($"@{d.TrimStart('@')}", StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        // Admin/service accounts shouldn't be part of the simulated workforce: their activity looks
        // wrong in reports and they're often the account an operator is using interactively.
        string[] adminMarkers = ["admin", "svc", "service", "sync", "breakglass", "noreply", "no-reply"];
        var local = persona.UserPrincipalName.Split('@')[0];

        return adminMarkers.Any(m => local.Contains(m, StringComparison.OrdinalIgnoreCase));
    }

    private Persona BuildPersona(
        string id,
        string upn,
        string displayName,
        string? givenName,
        string? jobTitle,
        string? department,
        string? officeLocation,
        bool hasCopilot)
    {
        var archetype = InferArchetype(jobTitle, department);

        // Traits are derived from stable directory attributes, so the same person behaves the same
        // way on every run without persisting per-user state.
        var rng = DeterministicRandom.For(options.Simulation.Seed, "persona", id);

        var traits = BaseTraitsFor(archetype) with
        {
            Chattiness = Jitter(BaseTraitsFor(archetype).Chattiness, rng),
            MailVolume = Jitter(BaseTraitsFor(archetype).MailVolume, rng),
            FileVolume = Jitter(BaseTraitsFor(archetype).FileVolume, rng),
            CopilotAffinity = Jitter(BaseTraitsFor(archetype).CopilotAffinity, rng),
            Formality = Jitter(BaseTraitsFor(archetype).Formality, rng),
            MeetingLoad = Jitter(BaseTraitsFor(archetype).MeetingLoad, rng),
            TypicalReplyLatencyMinutes = 10 + rng.Next(0, 110),
            AfterHoursPropensity = Math.Clamp(BaseTraitsFor(archetype).AfterHoursPropensity + (rng.NextDouble() - 0.5) * 0.15, 0, 0.5)
        };

        var startHour = rng.Chance(0.25) ? 8 : rng.Chance(0.5) ? 9 : 10;
        var workingHours = new WorkingHours
        {
            Start = new TimeOnly(startHour, rng.Chance(0.5) ? 0 : 30),
            End = new TimeOnly(startHour + 8, rng.Chance(0.5) ? 0 : 30),
            LunchStart = new TimeOnly(12, rng.Chance(0.5) ? 0 : 30),
            LunchMinutes = 30 + rng.Next(0, 4) * 15
        };

        return new Persona
        {
            Id = id,
            UserPrincipalName = upn,
            DisplayName = displayName,
            GivenName = givenName,
            JobTitle = jobTitle,
            Department = department,
            OfficeLocation = officeLocation,
            TimeZoneId = InferTimeZone(officeLocation),
            Archetype = archetype,
            Traits = traits,
            WorkingHours = workingHours,
            HasCopilotLicence = hasCopilot
        };
    }

    private static double Jitter(double value, Random rng) =>
        Math.Clamp(value + (rng.NextDouble() - 0.5) * 0.3, 0.05, 1.0);

    private static PersonaTraits BaseTraitsFor(PersonaArchetype archetype) => archetype switch
    {
        PersonaArchetype.Executive => new PersonaTraits
        {
            Chattiness = 0.45, MailVolume = 0.85, FileVolume = 0.25, CopilotAffinity = 0.8,
            Formality = 0.7, MeetingLoad = 0.9, AfterHoursPropensity = 0.35
        },
        PersonaArchetype.Manager => new PersonaTraits
        {
            Chattiness = 0.7, MailVolume = 0.75, FileVolume = 0.5, CopilotAffinity = 0.7,
            Formality = 0.55, MeetingLoad = 0.75, AfterHoursPropensity = 0.2
        },
        PersonaArchetype.Engineer => new PersonaTraits
        {
            Chattiness = 0.75, MailVolume = 0.3, FileVolume = 0.7, CopilotAffinity = 0.75,
            Formality = 0.25, MeetingLoad = 0.35, AfterHoursPropensity = 0.25
        },
        PersonaArchetype.Sales => new PersonaTraits
        {
            Chattiness = 0.8, MailVolume = 0.9, FileVolume = 0.45, CopilotAffinity = 0.65,
            Formality = 0.5, MeetingLoad = 0.7, AfterHoursPropensity = 0.3
        },
        PersonaArchetype.Marketing => new PersonaTraits
        {
            Chattiness = 0.75, MailVolume = 0.6, FileVolume = 0.7, CopilotAffinity = 0.8,
            Formality = 0.4, MeetingLoad = 0.55
        },
        PersonaArchetype.Finance => new PersonaTraits
        {
            Chattiness = 0.4, MailVolume = 0.65, FileVolume = 0.85, CopilotAffinity = 0.5,
            Formality = 0.75, MeetingLoad = 0.45
        },
        PersonaArchetype.HumanResources => new PersonaTraits
        {
            Chattiness = 0.55, MailVolume = 0.7, FileVolume = 0.6, CopilotAffinity = 0.6,
            Formality = 0.7, MeetingLoad = 0.6
        },
        PersonaArchetype.Support => new PersonaTraits
        {
            Chattiness = 0.9, MailVolume = 0.6, FileVolume = 0.35, CopilotAffinity = 0.6,
            Formality = 0.35, MeetingLoad = 0.3
        },
        PersonaArchetype.Analyst => new PersonaTraits
        {
            Chattiness = 0.5, MailVolume = 0.45, FileVolume = 0.9, CopilotAffinity = 0.85,
            Formality = 0.5, MeetingLoad = 0.4
        },
        PersonaArchetype.Legal => new PersonaTraits
        {
            Chattiness = 0.35, MailVolume = 0.7, FileVolume = 0.8, CopilotAffinity = 0.45,
            Formality = 0.9, MeetingLoad = 0.4
        },
        _ => new PersonaTraits()
    };

    private static PersonaArchetype InferArchetype(string? jobTitle, string? department)
    {
        var haystack = $"{jobTitle} {department}".ToLowerInvariant();

        if (Contains(haystack, "ceo", "chief", "president", "founder", "vp ", "vice president", "director"))
        {
            return PersonaArchetype.Executive;
        }

        if (Contains(haystack, "manager", "head of", "lead", "supervisor"))
        {
            return PersonaArchetype.Manager;
        }

        if (Contains(haystack, "engineer", "developer", "architect", "devops", "sre", "technician", "it "))
        {
            return PersonaArchetype.Engineer;
        }

        if (Contains(haystack, "sales", "account executive", "business development", "partner"))
        {
            return PersonaArchetype.Sales;
        }

        if (Contains(haystack, "market", "brand", "communications", "content", "product manager"))
        {
            return PersonaArchetype.Marketing;
        }

        if (Contains(haystack, "financ", "account", "controller", "payroll", "audit"))
        {
            return PersonaArchetype.Finance;
        }

        if (Contains(haystack, "human resources", "hr", "people", "recruit", "talent"))
        {
            return PersonaArchetype.HumanResources;
        }

        if (Contains(haystack, "support", "service desk", "helpdesk", "customer success"))
        {
            return PersonaArchetype.Support;
        }

        if (Contains(haystack, "analyst", "data", "research", "insight", "scientist"))
        {
            return PersonaArchetype.Analyst;
        }

        if (Contains(haystack, "legal", "counsel", "compliance", "privacy"))
        {
            return PersonaArchetype.Legal;
        }

        if (Contains(haystack, "operations", "logistics", "facilities", "procurement", "supply"))
        {
            return PersonaArchetype.Operations;
        }

        return PersonaArchetype.Operations;
    }

    private static bool Contains(string haystack, params string[] needles) =>
        needles.Any(n => haystack.Contains(n, StringComparison.Ordinal));

    /// <summary>
    /// Maps a directory office location to an IANA time zone. Spreading personas across time zones
    /// is what makes activity trickle in through the day rather than arriving in one lump.
    /// </summary>
    private string InferTimeZone(string? officeLocation)
    {
        if (string.IsNullOrWhiteSpace(officeLocation))
        {
            return options.Simulation.DefaultTimeZone;
        }

        var location = officeLocation.ToLowerInvariant();

        (string[] Markers, string Zone)[] map =
        [
            (["london", "uk", "united kingdom", "england", "reading", "manchester"], "Europe/London"),
            (["dublin", "ireland"], "Europe/Dublin"),
            (["paris", "france"], "Europe/Paris"),
            (["madrid", "spain", "barcelona"], "Europe/Madrid"),
            (["berlin", "germany", "munich", "frankfurt"], "Europe/Berlin"),
            (["amsterdam", "netherlands"], "Europe/Amsterdam"),
            (["stockholm", "sweden"], "Europe/Stockholm"),
            (["new york", "nyc", "boston", "atlanta", "miami", "toronto"], "America/New_York"),
            (["chicago", "dallas", "houston", "austin"], "America/Chicago"),
            (["denver", "phoenix", "salt lake"], "America/Denver"),
            (["seattle", "redmond", "san francisco", "los angeles", "california", "vancouver"], "America/Los_Angeles"),
            (["sydney", "melbourne", "australia"], "Australia/Sydney"),
            (["singapore"], "Asia/Singapore"),
            (["tokyo", "japan"], "Asia/Tokyo"),
            (["bangalore", "bengaluru", "india", "hyderabad", "mumbai", "delhi"], "Asia/Kolkata"),
            (["dubai", "uae"], "Asia/Dubai"),
            (["johannesburg", "south africa"], "Africa/Johannesburg"),
            (["sao paulo", "são paulo", "brazil"], "America/Sao_Paulo")
        ];

        foreach (var (markers, zone) in map)
        {
            if (markers.Any(m => location.Contains(m, StringComparison.Ordinal)))
            {
                return zone;
            }
        }

        return options.Simulation.DefaultTimeZone;
    }
}
