using TaskForge.Domain.Jobs;

namespace TaskForge.UnitTests.Jobs;

public sealed class JobTests
{
    private static readonly DateTimeOffset CreatedAt =
        new(2026, 7, 20, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void New_job_starts_pending()
    {
        Job job = CreateJob();

        Assert.Equal(JobStatus.Pending, job.Status);
        Assert.Equal(0, job.Version);
        Assert.Null(job.QueuedAtUtc);
        Assert.Null(job.OwningWorkerId);
    }

    [Fact]
    public void Job_can_complete_successful_lifecycle()
    {
        Job job = CreateJob();
        DateTimeOffset queuedAt = CreatedAt.AddSeconds(1);
        DateTimeOffset startedAt = CreatedAt.AddSeconds(2);
        DateTimeOffset completedAt = CreatedAt.AddSeconds(3);

        job.Queue(queuedAt);
        job.StartProcessing("worker-01", startedAt.AddMinutes(1), startedAt);
        job.Complete(completedAt);

        Assert.Equal(JobStatus.Completed, job.Status);
        Assert.Equal(queuedAt, job.QueuedAtUtc);
        Assert.Equal(startedAt, job.StartedAtUtc);
        Assert.Equal(completedAt, job.CompletedAtUtc);
        Assert.Equal(completedAt, job.UpdatedAtUtc);
        Assert.Null(job.OwningWorkerId);
        Assert.Null(job.LeaseExpiresAtUtc);
        Assert.Equal(3, job.Version);
    }

    [Fact]
    public void Queuing_job_twice_is_rejected()
    {
        Job job = CreateJob();
        job.Queue(CreatedAt.AddSeconds(1));

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            () => job.Queue(CreatedAt.AddSeconds(2)));

        Assert.Equal("Only a pending job can be queued.", exception.Message);
    }

    [Fact]
    public void Pending_job_cannot_be_completed()
    {
        Job job = CreateJob();

        Assert.Throws<InvalidOperationException>(
            () => job.Complete(CreatedAt.AddSeconds(1)));
    }

    [Fact]
    public void Processing_requires_future_lease()
    {
        Job job = CreateJob();
        DateTimeOffset now = CreatedAt.AddSeconds(1);
        job.Queue(now);

        Assert.Throws<ArgumentOutOfRangeException>(
            () => job.StartProcessing("worker-01", now, now));
    }

    [Fact]
    public void Failed_job_is_scheduled_and_can_be_queued_for_retry()
    {
        Job job = CreateJob();
        DateTimeOffset queuedAt = CreatedAt.AddSeconds(1);
        DateTimeOffset startedAt = CreatedAt.AddSeconds(2);
        DateTimeOffset failedAt = CreatedAt.AddSeconds(3);
        DateTimeOffset retryAt = CreatedAt.AddSeconds(10);
        job.Queue(queuedAt);
        job.StartProcessing("worker-01", startedAt.AddMinutes(1), startedAt);

        job.Fail("Temporary failure.", retryAt, failedAt);

        Assert.Equal(JobStatus.Retrying, job.Status);
        Assert.Equal(1, job.RetryCount);
        Assert.Equal(retryAt, job.NextRetryAtUtc);
        Assert.Equal("Temporary failure.", job.LastError);
        Assert.Null(job.OwningWorkerId);
        Assert.Null(job.LeaseExpiresAtUtc);

        job.QueueRetry(retryAt);

        Assert.Equal(JobStatus.Queued, job.Status);
        Assert.Null(job.NextRetryAtUtc);
    }

    [Fact]
    public void Failure_without_retry_budget_is_dead_lettered()
    {
        Job job = CreateJob(maxRetries: 0);
        DateTimeOffset queuedAt = CreatedAt.AddSeconds(1);
        DateTimeOffset startedAt = CreatedAt.AddSeconds(2);
        job.Queue(queuedAt);
        job.StartProcessing("worker-01", startedAt.AddMinutes(1), startedAt);

        job.Fail(
            "Permanent failure.",
            CreatedAt.AddSeconds(10),
            CreatedAt.AddSeconds(3));

        Assert.Equal(JobStatus.DeadLettered, job.Status);
        Assert.Equal(1, job.RetryCount);
        Assert.Equal("Permanent failure.", job.LastError);
        Assert.Null(job.NextRetryAtUtc);
    }

    [Fact]
    public void Queued_job_is_cancelled_immediately()
    {
        Job job = CreateJob();
        job.Queue(CreatedAt.AddSeconds(1));

        job.RequestCancellation(CreatedAt.AddSeconds(2));

        Assert.True(job.CancellationRequested);
        Assert.Equal(JobStatus.Cancelled, job.Status);
    }

    [Fact]
    public void Processing_job_is_cancelled_after_request()
    {
        Job job = CreateJob();
        DateTimeOffset startedAt = CreatedAt.AddSeconds(2);
        job.Queue(CreatedAt.AddSeconds(1));
        job.StartProcessing("worker-01", startedAt.AddMinutes(1), startedAt);

        job.RequestCancellation(CreatedAt.AddSeconds(3));

        Assert.Equal(JobStatus.Processing, job.Status);
        Assert.True(job.CancellationRequested);

        job.Cancel(CreatedAt.AddSeconds(4));

        Assert.Equal(JobStatus.Cancelled, job.Status);
        Assert.Null(job.OwningWorkerId);
        Assert.Null(job.LeaseExpiresAtUtc);
    }

    [Fact]
    public void Processing_job_can_be_dead_lettered_immediately()
    {
        Job job = CreateJob();
        DateTimeOffset startedAt = CreatedAt.AddSeconds(2);
        job.Queue(CreatedAt.AddSeconds(1));
        job.StartProcessing("worker-01", startedAt.AddMinutes(1), startedAt);

        job.DeadLetter("No handler is registered.", CreatedAt.AddSeconds(3));

        Assert.Equal(JobStatus.DeadLettered, job.Status);
        Assert.Equal("No handler is registered.", job.LastError);
        Assert.Null(job.OwningWorkerId);
    }

    [Fact]
    public void Job_with_expired_lease_is_requeued()
    {
        Job job = CreateJob();
        DateTimeOffset startedAt = CreatedAt.AddSeconds(2);
        DateTimeOffset leaseExpiresAt = startedAt.AddMinutes(1);
        job.Queue(CreatedAt.AddSeconds(1));
        job.StartProcessing("worker-01", leaseExpiresAt, startedAt);

        job.RecoverExpiredLease(leaseExpiresAt);

        Assert.Equal(JobStatus.Queued, job.Status);
        Assert.Equal(leaseExpiresAt, job.QueuedAtUtc);
        Assert.Null(job.OwningWorkerId);
        Assert.Null(job.LeaseExpiresAtUtc);
        Assert.Contains("lease expired", job.LastError);
    }

    [Fact]
    public void Active_lease_cannot_be_recovered()
    {
        Job job = CreateJob();
        DateTimeOffset startedAt = CreatedAt.AddSeconds(2);
        DateTimeOffset leaseExpiresAt = startedAt.AddMinutes(1);
        job.Queue(CreatedAt.AddSeconds(1));
        job.StartProcessing("worker-01", leaseExpiresAt, startedAt);

        Assert.Throws<InvalidOperationException>(
            () => job.RecoverExpiredLease(leaseExpiresAt.AddTicks(-1)));
    }

    private static Job CreateJob(int maxRetries = 3) => new(
        Guid.Parse("7c077bba-bab3-4e20-a47f-a0ec51838a18"),
        "generate-report",
        """{"reportName":"Monthly report"}""",
        JobPriority.High,
        maxRetries,
        timeoutSeconds: 30,
        CreatedAt);
}