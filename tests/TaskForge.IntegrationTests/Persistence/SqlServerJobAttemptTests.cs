using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

using TaskForge.Application.Jobs;
using TaskForge.Application.Jobs.Models;
using TaskForge.Application.Workers;
using TaskForge.Domain.Jobs;
using TaskForge.Infrastructure.Persistence;

namespace TaskForge.IntegrationTests.Persistence;

[Collection("SQL Server")]
public sealed class SqlServerJobAttemptTests(SqlServerFixture fixture) : SqlServerTest(fixture)
{
    private static readonly DateTimeOffset Now = new(2026, 9, 17, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Recovery_closes_running_attempt_and_reexecution_uses_next_number()
    {
        Guid jobId = await AddQueuedJobAsync();
        JobAttempt first;
        await using (TaskForgeDbContext context = new(DatabaseOptions))
        {
            EfCoreJobStore store = new(context);
            first = (await store.DistributeAtAsync("test-app", "lost-worker", ["example"], TimeSpan.FromSeconds(5), Now))!.Attempt;
        }

        DateTimeOffset recoveredAt = Now.AddMinutes(1);
        await using (TaskForgeDbContext context = new(DatabaseOptions))
        {
            EfCoreJobStore store = new(context);
            Assert.Equal(1, await store.RecoverExpiredLeasesAsync(recoveredAt));
            Assert.Equal(0, await store.RecoverExpiredLeasesAsync(recoveredAt));
        }

        await using (TaskForgeDbContext context = new(DatabaseOptions))
        {
            EfCoreJobStore store = new(context);
            Assert.Null(await store.DistributeAtAsync("test-app", "replacement-worker", ["example"], TimeSpan.FromSeconds(5), recoveredAt));
            JobExecutionAssignment assignment = (await store.DistributeAtAsync("test-app", "replacement-worker", ["example"], TimeSpan.FromSeconds(5), recoveredAt.AddSeconds(5)))!;
            Assert.Equal(1, assignment.Job.RetryCount);
            Assert.Equal(2, assignment.Attempt.AttemptNumber);
        }

        await using (TaskForgeDbContext context = new(DatabaseOptions))
        {
            EfCoreExecutionStore executions = new(context, new JobRetryPolicy(Options.Create(new WorkerOptions())));
            ExecutionResult result = await executions.TransitionAsync(new("test-app", jobId, first.Id, first.WorkerId), new ExecutionReport.Complete(null));
            Assert.Equal(ExecutionResult.Stale, result);
        }

        await using TaskForgeDbContext readContext = new(DatabaseOptions);
        IReadOnlyList<JobAttempt> attempts = (await new EfCoreJobStore(readContext).GetAttemptsAsync(jobId))!.Attempts;
        Assert.Equal([1, 2], attempts.Select(attempt => attempt.AttemptNumber));
        Assert.Equal(JobAttemptOutcome.TimedOut, attempts[0].Outcome);
        Assert.Equal("Timeout", attempts[0].ErrorCode);
        Assert.Equal(recoveredAt, attempts[0].FinishedAtUtc);
        Assert.Equal(60_000, attempts[0].DurationMilliseconds);
        Assert.Equal(JobAttemptOutcome.Running, attempts[1].Outcome);
        Assert.Equal("replacement-worker", attempts[1].WorkerId);
        Assert.Null(attempts[1].FinishedAtUtc);
    }

    private async Task<Guid> AddQueuedJobAsync()
    {
        Job job = new(Guid.NewGuid(), "test-app", "example", "{}", JobPriority.Normal, 2, 5, Now);
        job.Queue(Now);
        await using TaskForgeDbContext context = new(DatabaseOptions);
        await new EfCoreJobStore(context).AddAsync(job);
        return job.Id;
    }
}
