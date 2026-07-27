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

        if (CancellationRequested)
        {
            throw new InvalidOperationException(
                "A job with cancellation requested cannot be completed.");
        }

        Status = JobStatus.Completed;
        CompletedAtUtc = now;
        LastError = null;
        NextRetryAtUtc = null;
        ClearOwnership();
        Touch(now);
    }

    public void Fail(
        string error,
        DateTimeOffset nextRetryAtUtc,
        DateTimeOffset now)
    {
        EnsureProcessing();

        if (CancellationRequested)
        {
            throw new InvalidOperationException(
                "A job with cancellation requested cannot be retried.");
        }

        if (string.IsNullOrWhiteSpace(error))
        {
            throw new ArgumentException("An error is required.", nameof(error));
        }

        if (nextRetryAtUtc <= now)
        {
            throw new ArgumentOutOfRangeException(
                nameof(nextRetryAtUtc),
                "The next retry must be in the future.");
        }

        RetryCount++;
        LastError = error;
        ClearOwnership();

        if (RetryCount <= MaxRetries)
        {
            Status = JobStatus.Retrying;
            NextRetryAtUtc = nextRetryAtUtc;
        }
        else
        {
            Status = JobStatus.DeadLettered;
            NextRetryAtUtc = null;
        }

        Touch(now);
    }

    public void QueueRetry(DateTimeOffset now)
    {
        if (Status != JobStatus.Retrying)
        {
            throw new InvalidOperationException("Only a retrying job can be queued.");
        }

        if (NextRetryAtUtc is null || NextRetryAtUtc > now)
        {
            throw new InvalidOperationException("The job retry is not due yet.");
        }

        Status = JobStatus.Queued;
        QueuedAtUtc = now;
        NextRetryAtUtc = null;
        Touch(now);
    }

    public void RequestCancellation(DateTimeOffset now)
    {
        if (Status is JobStatus.Completed or JobStatus.DeadLettered or JobStatus.Cancelled)
        {
            throw new InvalidOperationException(
                "A finished job cannot be cancelled.");
        }

        if (CancellationRequested)
        {
            return;
        }

        CancellationRequested = true;

        if (Status is JobStatus.Pending or JobStatus.Queued or JobStatus.Retrying)
        {
            Status = JobStatus.Cancelled;
            NextRetryAtUtc = null;
            ClearOwnership();
        }

        Touch(now);
    }

    public void Cancel(DateTimeOffset now)
    {
        EnsureProcessing();

        if (!CancellationRequested)
        {
            throw new InvalidOperationException(
                "Cancellation must be requested before cancelling a processing job.");
        }

        Status = JobStatus.Cancelled;
        NextRetryAtUtc = null;
        ClearOwnership();
        Touch(now);
    }

    public void DeadLetter(string error, DateTimeOffset now)
    {
        EnsureProcessing();

        if (CancellationRequested)
        {
            throw new InvalidOperationException(
                "A job with cancellation requested cannot be dead-lettered.");
        }

        if (string.IsNullOrWhiteSpace(error))
        {
            throw new ArgumentException("An error is required.", nameof(error));
        }

        Status = JobStatus.DeadLettered;
        LastError = error;
        NextRetryAtUtc = null;
        ClearOwnership();
        Touch(now);
    }

    private void EnsureProcessing()
    {
        if (Status != JobStatus.Processing)
        {
            throw new InvalidOperationException("Only a processing job can be updated.");
        }
    }

    private void ClearOwnership()
    {
        OwningWorkerId = null;
        LeaseExpiresAtUtc = null;
    }

    private void Touch(DateTimeOffset now)
    {
        UpdatedAtUtc = now;
        Version++;
    }
}
