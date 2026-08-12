using System.Text.Json;
using System.Text.Json.Serialization;
using TenantPulse.Core.Activities;
using TenantPulse.Core.Storylines;

namespace TenantPulse.Engine.Storylines;

/// <summary>
/// Loads the storyline catalogue from JSON. Storylines are data, not code, so a tenant can be given
/// its own narrative (an industry-specific bid, a regulatory programme) without a rebuild.
/// </summary>
public static class StorylineCatalogueLoader
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public static async Task<IReadOnlyList<Storyline>> LoadAsync(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException(
                $"Storyline catalogue not found at '{path}'. Copy config/storylines.json from the repo.", path);
        }

        await using var stream = File.OpenRead(path);
        var raw = await JsonSerializer
            .DeserializeAsync<List<StorylineDto>>(stream, SerializerOptions, cancellationToken)
            .ConfigureAwait(false);

        if (raw is null || raw.Count == 0)
        {
            return [];
        }

        return [.. raw.Select(Map)];
    }

    private static Storyline Map(StorylineDto dto) => new()
    {
        Id = dto.Id,
        Title = dto.Title,
        Summary = dto.Summary,
        Weight = dto.Weight <= 0 ? 1 : dto.Weight,
        Roles = [.. dto.Roles.Select(r => new StorylineRole
        {
            Name = r.Name,
            PreferredDepartment = r.PreferredDepartment,
            PreferredArchetypes = [.. r.PreferredArchetypes.Select(a => new PersonaArchetypeName(a))]
        })],
        Beats = [.. dto.Beats.Select(b => new StorylineBeat
        {
            Id = b.Id,
            DayOffset = b.DayOffset,
            Kind = Enum.TryParse<ActivityKind>(b.Kind, ignoreCase: true, out var kind)
                ? kind
                : throw new InvalidOperationException(
                    $"Storyline '{dto.Id}' beat '{b.Id}' has unknown kind '{b.Kind}'."),
            ActorRole = b.ActorRole,
            TargetRoles = b.TargetRoles,
            Topic = b.Topic,
            PreferredHour = b.PreferredHour,
            Hints = b.Hints
        })]
    };

    private sealed class StorylineDto
    {
        public string Id { get; set; } = string.Empty;

        public string Title { get; set; } = string.Empty;

        public string Summary { get; set; } = string.Empty;

        public double Weight { get; set; } = 1;

        public List<RoleDto> Roles { get; set; } = [];

        public List<BeatDto> Beats { get; set; } = [];
    }

    private sealed class RoleDto
    {
        public string Name { get; set; } = string.Empty;

        public List<string> PreferredArchetypes { get; set; } = [];

        public string? PreferredDepartment { get; set; }
    }

    private sealed class BeatDto
    {
        public string Id { get; set; } = string.Empty;

        public int DayOffset { get; set; }

        public string Kind { get; set; } = string.Empty;

        public string ActorRole { get; set; } = string.Empty;

        public List<string> TargetRoles { get; set; } = [];

        public string Topic { get; set; } = string.Empty;

        public int? PreferredHour { get; set; }

        public Dictionary<string, string> Hints { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }
}
