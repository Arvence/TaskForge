using System.Text.Json;
using TaskForge.Application.Jobs.Models;
using TaskForge.Domain.Jobs;

namespace TaskForge.Api.Jobs;

public sealed record SubmitJobRequest
{
    public string Type { get; init; } = string.Empty;
    public JsonElement Payload { get; init; }
    public JobPriority Priority { get; init; } = JobPriority.Normal;
    public int MaxRetries { get; init; } = 3;
    public int TimeoutSeconds { get; init; } = 30;

    public SubmitJobCommand ToCommand() => new(
        Type,
        Payload.ValueKind is JsonValueKind.Undefined
            ? string.Empty
            : Payload.GetRawText(),
        Priority,
        MaxRetries,
        TimeoutSeconds);
}
