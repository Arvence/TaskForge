namespace TaskForge.Domain.Jobs;

public enum JobStatus
{
    Pending = 0,
    Queued = 1,
    Processing = 2,
    Retrying = 3,
    Completed = 4,
    DeadLettered = 5,
    Cancelled = 6
}
