namespace TaskForge.SDK.Jobs;

public sealed record JobAttemptResponse(Guid JobId, int AttemptNumber, string WorkerId, DateTimeOffset StartedAtUtc, DateTimeOffset? FinishedAtUtc, long? DurationMilliseconds, JobAttemptOutcome Outcome, string? ErrorCode, string? ErrorMessage, Guid AttemptId, DateTimeOffset DeadlineAtUtc);
