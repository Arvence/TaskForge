using System.Globalization;
using System.Text.Json;

using TaskForge.SDK;
using TaskForge.SDK.Jobs;

namespace TaskForge.Cli;

internal sealed record SubmitCommand(string PayloadPath)
{
    private string? Type { get; init; }
    private JobPriority? Priority { get; init; }
    private int? MaxRetries { get; init; }
    private int? TimeoutSeconds { get; init; }
    private DateTimeOffset? ScheduledAtUtc { get; init; }
    private string? IdempotencyKey { get; init; }

    internal static SubmitCommand Parse(string[] args)
    {
        if (args.Length < 2 || string.IsNullOrWhiteSpace(args[1]) || args[1].StartsWith("--", StringComparison.Ordinal))
        {
            throw new ArgumentException("Use 'submit <payload.json>' followed by any submission options.");
        }

        SubmitCommand command = new(args[1]);
        HashSet<string> options = new(StringComparer.Ordinal);
        for (int index = 2; index < args.Length; index += 2)
        {
            string option = args[index];
            if (index + 1 >= args.Length || string.IsNullOrWhiteSpace(args[index + 1]))
            {
                throw new ArgumentException($"Provide a value for {option}.");
            }

            if (!options.Add(option))
            {
                throw new ArgumentException($"Specify {option} only once.");
            }

            string value = args[index + 1];
            command = option switch
            {
                "--type" => command with { Type = value },
                "--priority" => command with { Priority = ParsePriority(value) },
                "--max-retries" => command with { MaxRetries = ParseInteger(value, option) },
                "--timeout-seconds" => command with { TimeoutSeconds = ParseInteger(value, option) },
                "--run-at" => command with { ScheduledAtUtc = ParseScheduledTime(value) },
                "--idempotency-key" => command with { IdempotencyKey = value },
                _ => throw new ArgumentException($"Unknown submission option '{option}'.")
            };
        }

        return command;
    }

    internal async Task<JobResponse> ExecuteAsync(TaskForgeClient client)
    {
        await using FileStream file = File.OpenRead(PayloadPath);
        using JsonDocument payload = await JsonDocument.ParseAsync(file);
        SubmitJobRequest request = Type is null ? new(payload.RootElement) : new(Type, payload.RootElement);
        return await client.SubmitAsync(request with
        {
            Priority = Priority ?? request.Priority,
            MaxRetries = MaxRetries ?? request.MaxRetries,
            TimeoutSeconds = TimeoutSeconds ?? request.TimeoutSeconds,
            ScheduledAtUtc = ScheduledAtUtc
        }, IdempotencyKey);
    }

    private static JobPriority ParsePriority(string value)
    {
        if (Enum.TryParse(value, ignoreCase: true, out JobPriority priority) && value.Equals(priority.ToString(), StringComparison.OrdinalIgnoreCase) && Enum.IsDefined(priority))
        {
            return priority;
        }

        throw new ArgumentException("Use Low, Normal, High, or Critical for --priority.");
    }

    private static int ParseInteger(string value, string option) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int number)
            ? number
            : throw new ArgumentException($"Use an integer for {option}.");

    private static DateTimeOffset ParseScheduledTime(string value)
    {
        bool hasOffset = value.EndsWith('Z') || (value.Length >= 6 && value[^6] is '+' or '-');
        if (hasOffset && DateTimeOffset.TryParseExact(value, ["yyyy-MM-dd'T'HH:mmK", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFFK"], CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTimeOffset scheduledAt))
        {
            return scheduledAt.ToUniversalTime();
        }

        throw new ArgumentException("Use an ISO 8601 timestamp with an explicit UTC offset for --run-at.");
    }
}
