namespace TaskForge.SDK.Execution;

public sealed record JobExecutionContext(Guid JobId, string ApplicationId, Guid AttemptId, int AttemptNumber, string WorkerId, string JobType, DateTimeOffset StartedAtUtc, DateTimeOffset DeadlineAtUtc);
