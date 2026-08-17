using System.Text.Json.Serialization;

namespace TenantPulse.Core.Configuration;
/// <summary>
/// The settings an operator can change while the simulator is running, without a redeploy.
/// <para>
/// Every value is nullable and means "leave the configured value alone" when null, so an operator
/// who has only ever touched the volume dial does not silently freeze the rest of the configuration
/// at whatever it happened to be the day they touched it.
/// </para>
/// </summary>
public sealed record RuntimeSettings
{
    /// <summary>See <see cref="SimulationOptions.ActivityIntensity"/>.</summary>
    public double? ActivityIntensity { get; init; }

    /// <summary>See <see cref="LimitsOptions.MaxActivitiesPerUserPerDay"/>.</summary>
    public int? MaxActivitiesPerUserPerDay { get; init; }

    /// <summary>See <see cref="LimitsOptions.MaxActivitiesPerTenantPerHour"/>.</summary>
    public int? MaxActivitiesPerTenantPerHour { get; init; }

    /// <summary>Who changed it last, for the audit line in the admin UI.</summary>
    public string? UpdatedBy { get; init; }

    public DateTimeOffset? UpdatedUtc { get; init; }

    [JsonIgnore]
    public bool IsEmpty =>
        ActivityIntensity is null &&
        MaxActivitiesPerUserPerDay is null &&
        MaxActivitiesPerTenantPerHour is null;

    /// <summary>Overlays this onto live options. Null members are left as configured.</summary>
    public void ApplyTo(TenantPulseOptions options)
    {
        if (ActivityIntensity is { } intensity)
        {
            options.Simulation.ActivityIntensity = Math.Clamp(intensity, 0.1, 20.0);
        }

        if (MaxActivitiesPerUserPerDay is { } perUser)
        {
            options.Limits.MaxActivitiesPerUserPerDay = Math.Clamp(perUser, 1, 500);
        }

        if (MaxActivitiesPerTenantPerHour is { } perHour)
        {
            options.Limits.MaxActivitiesPerTenantPerHour = Math.Clamp(perHour, 1, 5000);
        }
    }

    /// <summary>Snapshots what is currently in force, so the admin UI can show it.</summary>
    public static RuntimeSettings CaptureFrom(TenantPulseOptions options) => new()
    {
        ActivityIntensity = options.Simulation.ActivityIntensity,
        MaxActivitiesPerUserPerDay = options.Limits.MaxActivitiesPerUserPerDay,
        MaxActivitiesPerTenantPerHour = options.Limits.MaxActivitiesPerTenantPerHour
    };
}

/// <summary>
/// Where runtime settings live between restarts.
/// <para>
/// A hosted container is replaced on every deployment and restarted at Azure's discretion, so a
/// change made in the admin UI has to outlive the process or it is a lie.
/// </para>
/// </summary>
public interface IRuntimeSettingsStore
{
    Task<RuntimeSettings?> LoadAsync(CancellationToken cancellationToken);

    Task SaveAsync(RuntimeSettings settings, CancellationToken cancellationToken);
}
