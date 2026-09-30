namespace TaskForge.Domain.Workers;

public sealed class WorkerState
{
    private WorkerState() { }

    public string Id { get; private set; } = string.Empty;
    public WorkerStatus Status { get; private set; }
    public Guid? CurrentJobId { get; private set; }
    public DateTimeOffset StartedAtUtc { get; private set; }
    public DateTimeOffset LastHeartbeatAtUtc { get; private set; }
    public DateTimeOffset? StoppedAtUtc { get; private set; }
    public long Version { get; private set; }
}
