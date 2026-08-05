using TaskForge.Debugging.Api;

namespace TaskForge.Debugging.Commands;

internal enum DebugCommandKind
{
    Dashboard,
    Jobs,
    Help
}

internal sealed record DebugCommand(DebugCommandKind Kind, JobFilters? Filters = null)
{
    private static readonly string[] Statuses =
    [
        "Pending",
        "Queued",
        "Processing",
        "Retrying",
        "Completed",
        "DeadLettered",
        "Cancelled"
    ];

    private static readonly string[] Priorities =
    ["Low", "Normal", "High", "Critical"];

    public static DebugCommand Parse(IReadOnlyList<string> arguments)
    {
        if (arguments.Count == 0)
        {
            return new DebugCommand(DebugCommandKind.Dashboard);
        }

        string command = arguments[0].ToLowerInvariant();
        if (command is "help" or "--help" or "-h")
        {
            RequireNoExtraArguments(arguments);
            return new DebugCommand(DebugCommandKind.Help);
        }

        if (command is "dashboard" or "status")
        {
            RequireNoExtraArguments(arguments);
            return new DebugCommand(DebugCommandKind.Dashboard);
        }

        if (command != "jobs")
        {
            throw new ArgumentException(
                $"Unknown command '{arguments[0]}'. Run with --help for usage.");
        }

        Dictionary<string, string> options = ParseOptions(arguments.Skip(1));
        string? status = NormalizeChoice(
            options.GetValueOrDefault("--status"),
            Statuses,
            "status");
        string? priority = NormalizeChoice(
            options.GetValueOrDefault("--priority"),
            Priorities,
            "priority");
        string? type = options.GetValueOrDefault("--type");

        if (type is not null && string.IsNullOrWhiteSpace(type))
        {
            throw new ArgumentException("--type cannot be blank.");
        }

        return new DebugCommand(
            DebugCommandKind.Jobs,
            new JobFilters(status, type, priority));
    }

    private static Dictionary<string, string> ParseOptions(
        IEnumerable<string> arguments)
    {
        HashSet<string> allowed =
            new(["--status", "--type", "--priority"], StringComparer.OrdinalIgnoreCase);
        Dictionary<string, string> result = new(StringComparer.OrdinalIgnoreCase);
        string[] items = [.. arguments];

        for (int index = 0; index < items.Length; index += 2)
        {
            string option = items[index];
            if (!allowed.Contains(option))
            {
                throw new ArgumentException($"Unknown jobs option '{option}'.");
            }

            if (index + 1 >= items.Length
                || items[index + 1].StartsWith("--", StringComparison.Ordinal))
            {
                throw new ArgumentException($"{option} requires a value.");
            }

            if (!result.TryAdd(option, items[index + 1]))
            {
                throw new ArgumentException($"{option} can only be provided once.");
            }
        }

        return result;
    }

    private static string? NormalizeChoice(
        string? value,
        IEnumerable<string> choices,
        string description)
    {
        if (value is null)
        {
            return null;
        }

        string? match = choices.FirstOrDefault(
            choice => choice.Equals(value, StringComparison.OrdinalIgnoreCase));
        return match
            ?? throw new ArgumentException(
                $"Unknown {description} '{value}'. Expected: "
                + string.Join(", ", choices));
    }

    private static void RequireNoExtraArguments(IReadOnlyCollection<string> arguments)
    {
        if (arguments.Count > 1)
        {
            throw new ArgumentException(
                $"The '{arguments.First()}' command does not accept options.");
        }
    }
}
