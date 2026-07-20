using System.Text.Json;
using TaskForge.Domain.Jobs;

namespace TaskForge.Api.Jobs;

public sealed record JobResponse(
    Guid Id,
    string Type,
    JsonElement Payload,
    JobPriority Priority,
    JobStatus Status,
    int MaxRetries,
    int RetryCount,
    int TimeoutSeconds,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    DateTimeOffset? QueuedAtUtc)
{
    public static JobResponse From(Job job) => new(
        job.Id,
        job.Type,
        JsonSerializer.Deserialize<JsonElement>(job.PayloadJson),
        job.Priority,
        job.Status,
        job.MaxRetries,
        job.RetryCount,
        job.TimeoutSeconds,
        job.CreatedAtUtc,
        job.UpdatedAtUtc,
        job.QueuedAtUtc);
}
