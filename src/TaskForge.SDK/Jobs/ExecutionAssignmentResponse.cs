using System.Text.Json;

namespace TaskForge.SDK.Jobs;

public sealed record ExecutionAssignmentResponse(Guid JobId, string ApplicationId, Guid AttemptId, int AttemptNumber, string WorkerId, string Type, JsonElement Payload, int TimeoutSeconds, DateTimeOffset StartedAtUtc, DateTimeOffset DeadlineAtUtc, DateTimeOffset LeaseExpiresAtUtc);
