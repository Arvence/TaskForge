using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;

using TaskForge.Application.Jobs.Models;
using TaskForge.Domain.Jobs;

namespace TaskForge.Api.Jobs;

public sealed record SubmitJobRequest
{
    private const string DefaultType = "general";

    public string ApplicationId { get; init; } = string.Empty;
    [DefaultValue(DefaultType)]
    public string Type { get; init; } = DefaultType;
    public JsonElement Payload { get; init; }
    public JobPriority Priority { get; init; } = JobPriority.Normal;
    public int MaxRetries { get; init; } = 3;
    public int TimeoutSeconds { get; init; } = 30;
    [JsonConverter(typeof(ScheduledAtUtcJsonConverter))]
    public DateTimeOffset? ScheduledAtUtc { get; init; }

    public SubmitJobCommand ToCommand() => new(
        ApplicationId,
        Type,
        Payload.ValueKind is JsonValueKind.Undefined
            ? string.Empty
            : Payload.GetRawText(),
        Priority,
        MaxRetries,
        TimeoutSeconds,
        ScheduledAtUtc);
}
