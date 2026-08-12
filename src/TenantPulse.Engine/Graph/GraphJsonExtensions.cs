using System.Text.Json;

namespace TenantPulse.Engine.Graph;

/// <summary>
/// Small helpers for reading Graph JSON defensively. Graph omits properties rather than returning
/// nulls, so every read has to cope with the property simply not being there.
/// </summary>
public static class GraphJsonExtensions
{
    /// <summary>Reads a string property, returning null when absent or not a string.</summary>
    public static string? GetStringOrNull(this JsonElement element, string propertyName)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (!element.TryGetProperty(propertyName, out var value))
        {
            return null;
        }

        return value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    }

    /// <summary>Reads a nested string, e.g. <c>from.emailAddress.address</c>.</summary>
    public static string? GetNestedString(this JsonElement element, params string[] path)
    {
        var current = element;

        foreach (var segment in path)
        {
            if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(segment, out var next))
            {
                return null;
            }

            current = next;
        }

        return current.ValueKind == JsonValueKind.String ? current.GetString() : null;
    }

    /// <summary>Enumerates the <c>value</c> array of a Graph collection response.</summary>
    public static IReadOnlyList<JsonElement> GetValueArray(this JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object ||
            !element.TryGetProperty("value", out var value) ||
            value.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return [.. value.EnumerateArray()];
    }

    public static bool GetBoolOrDefault(this JsonElement element, string propertyName, bool fallback = false)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(propertyName, out var value))
        {
            return fallback;
        }

        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => fallback
        };
    }
}
