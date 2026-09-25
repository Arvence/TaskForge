using TaskForge.Domain.Jobs;

namespace TaskForge.Api.Jobs;

public sealed record JobAttemptResponse(Guid JobId, int AttemptNumber, string WorkerId, DateTimeOffset StartedAtUtc, DateTimeOffset? FinishedAtUtc, long? DurationMilliseconds, JobAttemptOutcome Outcome, string? ErrorCode, string? ErrorMessage, Guid AttemptId, DateTimeOffset DeadlineAtUtc)
{
    public static JobAttemptResponse From(JobAttempt attempt, int timeoutSeconds) => new(
        attempt.JobId, attempt.AttemptNumber, attempt.WorkerId, attempt.StartedAtUtc, attempt.FinishedAtUtc,
        attempt.DurationMilliseconds, attempt.Outcome, attempt.ErrorCode, attempt.ErrorMessage, attempt.Id, attempt.StartedAtUtc.AddSeconds(timeoutSeconds));
}
