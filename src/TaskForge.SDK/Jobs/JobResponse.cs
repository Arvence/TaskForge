using System.Text.Json;

namespace TaskForge.SDK.Jobs;

public sealed record JobResponse(Guid Id, string ApplicationId, string Type, JsonElement Payload, string? IdempotencyKey, JsonElement? Result, JobPriority Priority, JobStatus Status, int MaxRetries, int RetryCount, int TimeoutSeconds, bool CancellationRequested, DateTimeOffset CreatedAtUtc, DateTimeOffset UpdatedAtUtc, DateTimeOffset? QueuedAtUtc, DateTimeOffset? StartedAtUtc, DateTimeOffset? CompletedAtUtc, DateTimeOffset? NextRetryAtUtc, string? OwningWorkerId, DateTimeOffset? LeaseExpiresAtUtc, string? LastError);
