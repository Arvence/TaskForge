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

    private static Job CreateJob() => new(
        Guid.Parse("7c077bba-bab3-4e20-a47f-a0ec51838a18"),
        "generate-report",
        """{"reportName":"Monthly report"}""",
        JobPriority.High,
        maxRetries: 3,
        timeoutSeconds: 30,
        CreatedAt);
}
