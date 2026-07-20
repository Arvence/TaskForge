namespace TaskForge.Domain.Workers;

public sealed class WorkerState
{
    private WorkerState() { }

    public WorkerState(string id, DateTimeOffset startedAtUtc)
    {
        Id = id;
        Status = WorkerStatus.Starting;
        StartedAtUtc = startedAtUtc;
        LastHeartbeatAtUtc = startedAtUtc;
    }

    public string Id { get; private set; } = string.Empty;
    public WorkerStatus Status { get; private set; }
    public Guid? CurrentJobId { get; private set; }
    public DateTimeOffset StartedAtUtc { get; private set; }
    public DateTimeOffset LastHeartbeatAtUtc { get; private set; }
    public DateTimeOffset? StoppedAtUtc { get; private set; }
    public long Version { get; private set; }

    public void MarkIdle(DateTimeOffset now)
    {
        Status = WorkerStatus.Idle;
        CurrentJobId = null;
        UpdateHeartbeat(now);
    }

    public void AssignJob(Guid jobId, DateTimeOffset now)
    {
        if (Status != WorkerStatus.Idle)
        {
            throw new InvalidOperationException("Only an idle worker can take a job.");
        }

        CurrentJobId = jobId;
        Status = WorkerStatus.Busy;
        UpdateHeartbeat(now);
    }

    public void ReleaseJob(DateTimeOffset now)
    {
        CurrentJobId = null;
        Status = WorkerStatus.Idle;
        UpdateHeartbeat(now);
    }

    public void RecordHeartbeat(DateTimeOffset now)
    {
        UpdateHeartbeat(now);
    }

    public void BeginStopping(DateTimeOffset now)
    {
        Status = WorkerStatus.Stopping;
        UpdateHeartbeat(now);
    }

    public void MarkOffline(DateTimeOffset now)
    {
        Status = WorkerStatus.Offline;
        CurrentJobId = null;
        StoppedAtUtc = now;
        UpdateHeartbeat(now);
    }

    private void UpdateHeartbeat(DateTimeOffset now)
    {
        LastHeartbeatAtUtc = now;
        Version++;
    }
}
