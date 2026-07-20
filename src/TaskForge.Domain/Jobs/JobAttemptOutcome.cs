namespace TaskForge.Domain.Jobs;

public enum JobAttemptOutcome
{
    Running = 0,
    Succeeded = 1,
    Failed = 2,
    TimedOut = 3,
    Cancelled = 4,
    Abandoned = 5
}
