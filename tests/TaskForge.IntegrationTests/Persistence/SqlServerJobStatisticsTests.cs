using TaskForge.Application.Jobs.Models;
using TaskForge.Domain.Jobs;
using TaskForge.Infrastructure.Persistence;

namespace TaskForge.IntegrationTests.Persistence;

[Collection("SQL Server")]
public sealed class SqlServerJobStatisticsTests(SqlServerFixture fixture) : SqlServerTest(fixture)
{
    [Fact]
    public async Task Empty_database_returns_zero_counts_and_null_success_rate()
    {
        await using TaskForgeDbContext context = new(DatabaseOptions);

        JobStatistics stats = await new EfCoreJobStore(context).GetAsync();

        Assert.Equal(new JobStatusCounts(), stats.CountsByStatus);
        Assert.Equal(0L, stats.TotalJobs);
        Assert.Null(stats.SuccessRatePercent);
    }

    [Fact]
    public async Task Stats_include_all_current_states_across_job_types_priorities_and_dates()
    {
        DateTimeOffset now = new(2026, 9, 14, 12, 0, 0, TimeSpan.Zero);
        await using (TaskForgeDbContext context = new(DatabaseOptions))
        {
            foreach (JobStatus status in Enum.GetValues<JobStatus>())
            {
                for (int index = 0; index <= (int)status; index++)
                {
                    DateTimeOffset createdAt = now.AddDays(-index * 365);
                    Job job = new(Guid.NewGuid(), index % 2 == 0 ? "http-request" : "generate-report", "{}", index % 2 == 0 ? JobPriority.High : JobPriority.Low, 3, 30, createdAt);
                    if (status != JobStatus.Pending)
                    {
                        job.Queue(createdAt);
                    }

                    if (status == JobStatus.Cancelled)
                    {
                        job.RequestCancellation(createdAt);
                    }
                    else if (status is JobStatus.Processing or JobStatus.Retrying or JobStatus.Completed or JobStatus.DeadLettered)
                    {
                        job.StartProcessing("stats-test", createdAt.AddMinutes(1), createdAt);
                        switch (status)
                        {
                            case JobStatus.Retrying:
                                job.Fail("Retryable failure", createdAt.AddMinutes(2), createdAt);
                                break;
                            case JobStatus.Completed:
                                job.Complete(createdAt);
                                break;
                            case JobStatus.DeadLettered:
                                job.DeadLetter("Permanent failure", createdAt);
                                break;
                        }
                    }

                    context.Jobs.Add(job);
                }
            }

            await context.SaveChangesAsync();
        }

        await using TaskForgeDbContext readContext = new(DatabaseOptions);
        JobStatistics stats = await new EfCoreJobStore(readContext).GetAsync();

        Assert.Equal(new JobStatusCounts(1, 2, 3, 4, 5, 6, 7), stats.CountsByStatus);
        Assert.Equal(28L, stats.TotalJobs);
        Assert.Equal(45.45m, stats.SuccessRatePercent);
        Assert.Empty(readContext.ChangeTracker.Entries());
    }

    [Fact]
    public async Task Retrying_counts_jobs_waiting_for_retry_instead_of_retry_attempts()
    {
        DateTimeOffset now = new(2026, 9, 14, 12, 0, 0, TimeSpan.Zero);
        await using TaskForgeDbContext context = new(DatabaseOptions);
        Job job = new(Guid.NewGuid(), "http-request", "{}", JobPriority.Normal, 3, 30, now);
        job.Queue(now);
        job.StartProcessing("stats-test", now.AddMinutes(1), now);
        job.Fail("First failure", now.AddSeconds(1), now);
        job.QueueRetry(now.AddSeconds(1));
        job.StartProcessing("stats-test", now.AddMinutes(1), now.AddSeconds(1));
        job.Fail("Second failure", now.AddSeconds(3), now.AddSeconds(2));
        context.Jobs.Add(job);
        await context.SaveChangesAsync();
        EfCoreJobStore store = new(context);

        JobStatistics waitingStats = await store.GetAsync();

        Assert.Equal(2, job.RetryCount);
        Assert.Equal(new JobStatusCounts(Retrying: 1), waitingStats.CountsByStatus);
        Assert.Equal(1L, waitingStats.TotalJobs);
        Assert.Null(waitingStats.SuccessRatePercent);

        job.QueueRetry(now.AddSeconds(3));
        job.StartProcessing("stats-test", now.AddMinutes(1), now.AddSeconds(3));
        await context.SaveChangesAsync();

        JobStatistics runningStats = await store.GetAsync();
        Assert.Equal(new JobStatusCounts(Processing: 1), runningStats.CountsByStatus);
        Assert.Equal(1L, runningStats.TotalJobs);
        Assert.Null(runningStats.SuccessRatePercent);
    }

    [Fact]
    public async Task Cancellation_requested_job_counts_as_processing_until_cancellation_is_persisted()
    {
        DateTimeOffset now = new(2026, 9, 14, 12, 0, 0, TimeSpan.Zero);
        await using TaskForgeDbContext context = new(DatabaseOptions);
        Job job = new(Guid.NewGuid(), "http-request", "{}", JobPriority.Normal, 3, 30, now);
        job.Queue(now);
        job.StartProcessing("stats-test", now.AddMinutes(1), now);
        job.RequestCancellation(now.AddSeconds(1));
        context.Jobs.Add(job);
        await context.SaveChangesAsync();
        EfCoreJobStore store = new(context);

        JobStatistics requestedStats = await store.GetAsync();

        Assert.True(job.CancellationRequested);
        Assert.Equal(new JobStatusCounts(Processing: 1), requestedStats.CountsByStatus);
        Assert.Equal(1L, requestedStats.TotalJobs);
        Assert.Null(requestedStats.SuccessRatePercent);

        job.Cancel(now.AddSeconds(2));
        Assert.Equal(new JobStatusCounts(Processing: 1), (await store.GetAsync()).CountsByStatus);

        await context.SaveChangesAsync();

        JobStatistics cancelledStats = await store.GetAsync();
        Assert.Equal(new JobStatusCounts(Cancelled: 1), cancelledStats.CountsByStatus);
        Assert.Equal(1L, cancelledStats.TotalJobs);
        Assert.Null(cancelledStats.SuccessRatePercent);
    }

    [Fact]
    public async Task Completed_job_counts_only_its_current_state_and_missing_states_remain_zero()
    {
        DateTimeOffset now = new(2026, 9, 14, 12, 0, 0, TimeSpan.Zero);
        await using TaskForgeDbContext context = new(DatabaseOptions);
        Job job = new(Guid.NewGuid(), "http-request", "{}", JobPriority.Normal, 3, 30, now);
        job.Queue(now);
        context.Jobs.Add(job);
        await context.SaveChangesAsync();
        EfCoreJobStore store = new(context);

        Assert.Equal(new JobStatusCounts(Queued: 1), (await store.GetAsync()).CountsByStatus);

        job.StartProcessing("stats-test", now.AddMinutes(1), now);
        job.Fail("Retryable failure", now.AddSeconds(1), now);
        job.QueueRetry(now.AddSeconds(1));
        job.StartProcessing("stats-test", now.AddMinutes(1), now.AddSeconds(1));
        job.Complete(now.AddSeconds(2));
        await context.SaveChangesAsync();

        JobStatistics stats = await store.GetAsync();
        Assert.Equal(new JobStatusCounts(Completed: 1), stats.CountsByStatus);
        Assert.Equal(1L, stats.TotalJobs);
        Assert.Equal(100m, stats.SuccessRatePercent);
    }
}
