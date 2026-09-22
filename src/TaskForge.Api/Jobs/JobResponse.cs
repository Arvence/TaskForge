using System.Text.Json;

using TaskForge.Domain.Jobs;

namespace TaskForge.Api.Jobs;

public sealed record JobResponse(Guid Id, string ApplicationId, string Type, JsonElement Payload, string? IdempotencyKey, JsonElement? Result, JobPriority Priority, JobStatus Status, int MaxRetries, int RetryCount, int TimeoutSeconds, bool CancellationRequested, DateTimeOffset CreatedAtUtc, DateTimeOffset UpdatedAtUtc, DateTimeOffset? QueuedAtUtc, DateTimeOffset? StartedAtUtc, DateTimeOffset? CompletedAtUtc, DateTimeOffset? NextRetryAtUtc, string? OwningWorkerId, DateTimeOffset? LeaseExpiresAtUtc, string? LastError)
{
    public static JobResponse From(Job job) => new(
        job.Id,
        job.ApplicationId,
        job.Type,
        JsonSerializer.Deserialize<JsonElement>(job.PayloadJson),
        job.IdempotencyKey,
        job.ResultJson is null
            ? null
            : JsonSerializer.Deserialize<JsonElement>(job.ResultJson),
        job.Priority,
        job.Status,
        job.MaxRetries,
        job.RetryCount,
        job.TimeoutSeconds,
        job.CancellationRequested,
        job.CreatedAtUtc,
        job.UpdatedAtUtc,
        job.QueuedAtUtc,
        job.StartedAtUtc,
        job.CompletedAtUtc,
        job.NextRetryAtUtc,
        job.OwningWorkerId,
        job.LeaseExpiresAtUtc,
        job.LastError);
}
