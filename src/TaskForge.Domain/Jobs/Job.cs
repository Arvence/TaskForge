namespace TaskForge.Domain.Jobs;

public sealed class Job
{
    private Job() { }

    public Job(Guid id, string type, string payloadJson, JobPriority priority, int maxRetries, int timeoutSeconds, DateTimeOffset createdAtUtc)
    {
        Id = id;
        Type = type;
        PayloadJson = payloadJson;
        Priority = priority;
        MaxRetries = maxRetries;
        TimeoutSeconds = timeoutSeconds;
        CreatedAtUtc = createdAtUtc;
        UpdatedAtUtc = createdAtUtc;
        Status = JobStatus.Pending;
    }

    public Guid Id { get; private set; }
    public string Type { get; private set; } = string.Empty;
    public string PayloadJson { get; private set; } = string.Empty;
    public JobPriority Priority { get; private set; }
    public JobStatus Status { get; private set; }
    public int MaxRetries { get; private set; }
    public int RetryCount { get; private set; }
    public int TimeoutSeconds { get; private set; }
    public bool CancellationRequested { get; private set; }
    public DateTimeOffset CreatedAtUtc { get; private set; }
    public DateTimeOffset UpdatedAtUtc { get; private set; }
    public DateTimeOffset? QueuedAtUtc { get; private set; }
    public DateTimeOffset? StartedAtUtc { get; private set; }
    public DateTimeOffset? CompletedAtUtc { get; private set; }
    public DateTimeOffset? NextRetryAtUtc { get; private set; }
    public string? OwningWorkerId { get; private set; }
    public DateTimeOffset? LeaseExpiresAtUtc { get; private set; }
    public string? LastError { get; private set; }
    public long Version { get; private set; }

    public void Queue(DateTimeOffset now)
    {
        if (Status != JobStatus.Pending)
        {
            throw new InvalidOperationException("Only a pending job can be queued.");
        }

        Status = JobStatus.Queued;
        QueuedAtUtc = now;
        Touch(now);
    }

    public void StartProcessing(
        string workerId,
        DateTimeOffset leaseExpiresAtUtc,
        DateTimeOffset now)
    {
        if (Status != JobStatus.Queued)
        {
            throw new InvalidOperationException("Only a queued job can start processing.");
        }

        if (string.IsNullOrWhiteSpace(workerId))
        {
            throw new ArgumentException("A worker ID is required.", nameof(workerId));
        }

        if (leaseExpiresAtUtc <= now)
        {
            throw new ArgumentOutOfRangeException(
                nameof(leaseExpiresAtUtc),
                "The lease must expire in the future.");
        }

        Status = JobStatus.Processing;
        OwningWorkerId = workerId;
        LeaseExpiresAtUtc = leaseExpiresAtUtc;
        StartedAtUtc = now;
        Touch(now);
    }

    public void Complete(DateTimeOffset now)
    {
        if (Status != JobStatus.Processing)
        {
            throw new InvalidOperationException("Only a processing job can be completed.");
        }

        Status = JobStatus.Completed;
        CompletedAtUtc = now;
        OwningWorkerId = null;
        LeaseExpiresAtUtc = null;
        Touch(now);
    }

    private void Touch(DateTimeOffset now)
    {
        UpdatedAtUtc = now;
        Version++;
    }
}
