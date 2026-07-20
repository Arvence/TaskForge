using System.Text.Json;
using TaskForge.Domain.Jobs;

namespace TaskForge.Api.Jobs;

public sealed record SubmitJobRequest
{
    public string Type { get; init; } = string.Empty;
    public JsonElement Payload { get; init; }
    public JobPriority Priority { get; init; } = JobPriority.Normal;
    public int MaxRetries { get; init; } = 3;
    public int TimeoutSeconds { get; init; } = 30;

    public Dictionary<string, string[]> Validate()
    {
        Dictionary<string, string[]> errors = [];

        if (string.IsNullOrWhiteSpace(Type))
        {
            errors[nameof(Type)] = ["Job type is required."];
        }
        else if (Type.Trim().Length > 100)
        {
            errors[nameof(Type)] = ["Job type cannot exceed 100 characters."];
        }

        if (Payload.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
        {
            errors[nameof(Payload)] = ["Job payload is required."];
        }

        if (!Enum.IsDefined(Priority))
        {
            errors[nameof(Priority)] = ["Job priority is invalid."];
        }

        if (MaxRetries is < 0 or > 10)
        {
            errors[nameof(MaxRetries)] = ["Max retries must be between 0 and 10."];
        }

        if (TimeoutSeconds is < 1 or > 3600)
        {
            errors[nameof(TimeoutSeconds)] = ["Timeout seconds must be between 1 and 3600."];
        }

        return errors;
    }
}
