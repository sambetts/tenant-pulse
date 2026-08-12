namespace TenantPulse.Cli;

/// <summary>
/// Minimal argument parser. Deliberately hand-rolled: the CLI surface is small and stable, and this
/// avoids taking a dependency whose API has churned repeatedly.
/// </summary>
internal sealed class CommandLine
{
    private readonly Dictionary<string, string?> _switches = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _positional = [];

    private CommandLine() { }

    public string Command => _positional.Count > 0 ? _positional[0] : "help";

    public string[] RawArgs { get; private init; } = [];

    public static CommandLine Parse(string[] args)
    {
        var parsed = new CommandLine { RawArgs = args };

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];

            if (!arg.StartsWith("--", StringComparison.Ordinal))
            {
                parsed._positional.Add(arg);
                continue;
            }

            var name = arg[2..];

            if (name.Contains('=', StringComparison.Ordinal))
            {
                var parts = name.Split('=', 2);
                parsed._switches[parts[0]] = parts[1];
                continue;
            }

            var hasValue = i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal);
            parsed._switches[name] = hasValue ? args[++i] : null;
        }

        return parsed;
    }

    public bool Has(string name) => _switches.ContainsKey(name);

    public string? Value(string name) => _switches.TryGetValue(name, out var v) ? v : null;

    public int IntValue(string name, int fallback) =>
        int.TryParse(Value(name), out var parsed) ? parsed : fallback;

    public DateOnly DateValue(string name, DateOnly fallback) =>
        DateOnly.TryParse(Value(name), out var parsed) ? parsed : fallback;
}
