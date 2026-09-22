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

        JobStatistics stats = await new EfCoreJobStatisticsReader(context).GetAsync();

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
                    Job job = new(Guid.NewGuid(), "test-app", index % 2 == 0 ? "http-request" : "generate-report", "{}", index % 2 == 0 ? JobPriority.High : JobPriority.Low, 3, 30, createdAt);
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
        JobStatistics stats = await new EfCoreJobStatisticsReader(readContext).GetAsync();

        Assert.Equal(new JobStatusCounts(1, 2, 3, 4, 5, 6, 7), stats.CountsByStatus);
        Assert.Equal(28L, stats.TotalJobs);
        Assert.Equal(45.45m, stats.SuccessRatePercent);
    }

    [Fact]
    public async Task Changed_job_status_is_reflected_by_the_next_read_and_missing_states_remain_zero()
    {
        DateTimeOffset now = new(2026, 9, 14, 12, 0, 0, TimeSpan.Zero);
        await using TaskForgeDbContext context = new(DatabaseOptions);
        Job job = new(Guid.NewGuid(), "test-app", "http-request", "{}", JobPriority.Normal, 3, 30, now);
        job.Queue(now);
        context.Jobs.Add(job);
        await context.SaveChangesAsync();
        EfCoreJobStatisticsReader reader = new(context);

        Assert.Equal(new JobStatusCounts(Queued: 1), (await reader.GetAsync()).CountsByStatus);

        job.StartProcessing("stats-test", now.AddMinutes(1), now);
        job.Complete(now.AddSeconds(1));
        await context.SaveChangesAsync();

        JobStatistics stats = await reader.GetAsync();
        Assert.Equal(new JobStatusCounts(Completed: 1), stats.CountsByStatus);
        Assert.Equal(1L, stats.TotalJobs);
        Assert.Equal(100m, stats.SuccessRatePercent);
    }
}
