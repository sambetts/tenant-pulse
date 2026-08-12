using System.Security.Cryptography;
using System.Text;

namespace TenantPulse.Core;

/// <summary>
/// Seeded RNG helpers. Every random decision in tenant-pulse flows through here so a run is
/// reproducible: same seed + same day + same key produces the same plan.
/// </summary>
public static class DeterministicRandom
{
    /// <summary>Creates a <see cref="Random"/> deterministically derived from a seed and a string key.</summary>
    public static Random For(int seed, params string[] keyParts)
    {
        var key = string.Join('|', keyParts);
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"{seed}|{key}"));
        var derived = BitConverter.ToInt32(bytes, 0);
        return new Random(derived);
    }

    /// <summary>Picks one item using per-item weights. Returns default when the source is empty.</summary>
    public static T? WeightedPick<T>(this Random rng, IReadOnlyList<T> items, Func<T, double> weight)
    {
        if (items.Count == 0)
        {
            return default;
        }

        var total = items.Sum(i => Math.Max(0, weight(i)));
        if (total <= 0)
        {
            return items[rng.Next(items.Count)];
        }

        var roll = rng.NextDouble() * total;
        double cumulative = 0;
        foreach (var item in items)
        {
            cumulative += Math.Max(0, weight(item));
            if (roll <= cumulative)
            {
                return item;
            }
        }

        return items[^1];
    }

    /// <summary>Returns true with probability <paramref name="probability"/> (clamped to 0..1).</summary>
    public static bool Chance(this Random rng, double probability) =>
        rng.NextDouble() < Math.Clamp(probability, 0, 1);

    /// <summary>Random item, or default when empty.</summary>
    public static T? PickOrDefault<T>(this Random rng, IReadOnlyList<T> items) =>
        items.Count == 0 ? default : items[rng.Next(items.Count)];

    /// <summary>Fisher–Yates shuffle into a new list.</summary>
    public static List<T> Shuffled<T>(this Random rng, IEnumerable<T> source)
    {
        var list = source.ToList();
        for (var i = list.Count - 1; i > 0; i--)
        {
            var j = rng.Next(i + 1);
            (list[i], list[j]) = (list[j], list[i]);
        }

        return list;
    }
}
