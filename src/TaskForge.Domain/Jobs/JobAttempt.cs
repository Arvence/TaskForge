namespace TaskForge.Domain.Jobs;

public sealed class JobAttempt
{
    private JobAttempt() { }

    public JobAttempt(Guid id, Guid jobId, int attemptNumber, string workerId, DateTimeOffset startedAtUtc)
    {
        Id = id;
        JobId = jobId;
        AttemptNumber = attemptNumber;
        WorkerId = workerId;
        StartedAtUtc = startedAtUtc;
        Outcome = JobAttemptOutcome.Running;
    }

    public Guid Id { get; private set; }
    public Guid JobId { get; private set; }
    public int AttemptNumber { get; private set; }
    public string WorkerId { get; private set; } = string.Empty;
    public JobAttemptOutcome Outcome { get; private set; }
    public DateTimeOffset StartedAtUtc { get; private set; }
    public DateTimeOffset? FinishedAtUtc { get; private set; }
    public long? DurationMilliseconds { get; private set; }
    public string? ErrorCode { get; private set; }
    public string? ErrorMessage { get; private set; }
}
