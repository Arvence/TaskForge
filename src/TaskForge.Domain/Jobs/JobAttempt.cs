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

    public void Finish(JobAttemptOutcome outcome, DateTimeOffset finishedAtUtc, string? errorCode = null, string? errorMessage = null)
    {
        if (Outcome != JobAttemptOutcome.Running)
        {
            throw new InvalidOperationException("Only a running attempt can be finished.");
        }

        if (!Enum.IsDefined(outcome) || outcome == JobAttemptOutcome.Running)
        {
            throw new ArgumentOutOfRangeException(nameof(outcome));
        }

        Outcome = outcome;
        FinishedAtUtc = finishedAtUtc < StartedAtUtc ? StartedAtUtc : finishedAtUtc;
        DurationMilliseconds = (long)(FinishedAtUtc.Value - StartedAtUtc).TotalMilliseconds;
        ErrorCode = errorCode is { Length: > 100 } ? errorCode[..100] : errorCode;
        ErrorMessage = errorMessage is { Length: > 4000 } ? errorMessage[..4000] : errorMessage;
    }
}
