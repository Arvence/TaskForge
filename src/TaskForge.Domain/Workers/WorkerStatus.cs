namespace TaskForge.Domain.Workers;

public enum WorkerStatus
{
    Starting = 0,
    Idle = 1,
    Busy = 2,
    Stopping = 3,
    Offline = 4
}
