using Microsoft.EntityFrameworkCore;

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
            Job acquired = (await store.TryAcquireNextAsync("lost-worker", TimeSpan.FromSeconds(5), Now))!;
            first = (await store.TryStartAttemptAsync(acquired, Now))!;
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
            Job acquired = (await store.TryAcquireNextAsync("replacement-worker", TimeSpan.FromSeconds(5), recoveredAt))!;
            Assert.Equal(0, acquired.RetryCount);
            JobAttempt second = (await store.TryStartAttemptAsync(acquired, recoveredAt))!;
            Assert.Equal(2, second.AttemptNumber);
        }

        await using (TaskForgeDbContext context = new(DatabaseOptions))
        {
            first.Finish(JobAttemptOutcome.Succeeded, recoveredAt.AddSeconds(1));
            await new EfCoreJobStore(context).FinishAttemptAsync(first);
        }

        await using TaskForgeDbContext readContext = new(DatabaseOptions);
        IReadOnlyList<JobAttempt> attempts = (await new EfCoreJobStore(readContext).GetAttemptsAsync(jobId))!;
        Assert.Equal([1, 2], attempts.Select(attempt => attempt.AttemptNumber));
        Assert.Equal(JobAttemptOutcome.Abandoned, attempts[0].Outcome);
        Assert.Equal("LeaseExpired", attempts[0].ErrorCode);
        Assert.Equal(recoveredAt, attempts[0].FinishedAtUtc);
        Assert.Equal(60_000, attempts[0].DurationMilliseconds);
        Assert.Equal(JobAttemptOutcome.Running, attempts[1].Outcome);
        Assert.Equal("replacement-worker", attempts[1].WorkerId);
        Assert.Null(attempts[1].FinishedAtUtc);
    }

    [Fact]
    public async Task Lost_ownership_prevents_attempt_start()
    {
        Guid jobId = await AddQueuedJobAsync();
        await using TaskForgeDbContext workerContext = new(DatabaseOptions);
        EfCoreJobStore workerStore = new(workerContext);
        Job acquired = (await workerStore.TryAcquireNextAsync("lost-worker", TimeSpan.FromSeconds(5), Now))!;
        await using (TaskForgeDbContext recoveryContext = new(DatabaseOptions))
        {
            Assert.Equal(1, await new EfCoreJobStore(recoveryContext).RecoverExpiredLeasesAsync(Now.AddMinutes(1)));
        }

        Assert.Null(await workerStore.TryStartAttemptAsync(acquired, Now.AddMinutes(1)));

        await using TaskForgeDbContext readContext = new(DatabaseOptions);
        Assert.Empty((await new EfCoreJobStore(readContext).GetAttemptsAsync(jobId))!);
        Assert.Equal(JobStatus.Queued, (await readContext.Jobs.SingleAsync()).Status);
    }

    [Fact]
    public async Task Only_the_worker_that_acquires_a_job_can_start_an_attempt()
    {
        Guid jobId = await AddQueuedJobAsync();
        DbContextOptions<TaskForgeDbContext> options = new DbContextOptionsBuilder<TaskForgeDbContext>(DatabaseOptions)
            .AddInterceptors(new ConcurrentSaveInterceptor())
            .Options;
        await using TaskForgeDbContext firstContext = new(options);
        await using TaskForgeDbContext secondContext = new(options);
        EfCoreJobStore firstStore = new(firstContext);
        EfCoreJobStore secondStore = new(secondContext);
        Job?[] jobs = await Task.WhenAll(
            firstStore.TryAcquireNextAsync("worker-01", TimeSpan.FromSeconds(5), Now),
            secondStore.TryAcquireNextAsync("worker-02", TimeSpan.FromSeconds(5), Now));
        Job winner = Assert.Single(jobs.OfType<Job>());
        EfCoreJobStore winningStore = jobs[0] is null ? secondStore : firstStore;

        Assert.NotNull(await winningStore.TryStartAttemptAsync(winner, Now));

        await using TaskForgeDbContext readContext = new(DatabaseOptions);
        JobAttempt attempt = Assert.Single((await new EfCoreJobStore(readContext).GetAttemptsAsync(jobId))!);
        Assert.Equal(winner.OwningWorkerId, attempt.WorkerId);
        Assert.Equal(1, attempt.AttemptNumber);
    }

    [Fact]
    public async Task Concurrent_starts_for_the_same_acquisition_create_one_attempt()
    {
        Guid jobId = await AddQueuedJobAsync();
        await using (TaskForgeDbContext context = new(DatabaseOptions))
        {
            await new EfCoreJobStore(context).TryAcquireNextAsync("worker-01", TimeSpan.FromSeconds(5), Now);
        }

        DbContextOptions<TaskForgeDbContext> options = new DbContextOptionsBuilder<TaskForgeDbContext>(DatabaseOptions)
            .AddInterceptors(new ConcurrentSaveInterceptor())
            .Options;
        await using TaskForgeDbContext firstContext = new(options);
        await using TaskForgeDbContext secondContext = new(options);
        EfCoreJobStore firstStore = new(firstContext);
        EfCoreJobStore secondStore = new(secondContext);
        Job firstCopy = (await firstStore.FindAsync(jobId))!;
        Job secondCopy = (await secondStore.FindAsync(jobId))!;

        JobAttempt?[] results = await Task.WhenAll(
            firstStore.TryStartAttemptAsync(firstCopy, Now),
            secondStore.TryStartAttemptAsync(secondCopy, Now));

        Assert.Single(results.OfType<JobAttempt>());
        Assert.Single(results, attempt => attempt is null);
        await using TaskForgeDbContext readContext = new(DatabaseOptions);
        Assert.Equal(1, await readContext.JobAttempts.CountAsync());
        Assert.Equal(firstCopy.Version, (await readContext.Jobs.SingleAsync()).Version);
    }

    [Fact]
    public async Task Attempt_start_and_lease_recovery_commit_consistent_state()
    {
        Guid jobId = await AddQueuedJobAsync();
        await using (TaskForgeDbContext context = new(DatabaseOptions))
        {
            await new EfCoreJobStore(context).TryAcquireNextAsync("worker-01", TimeSpan.FromSeconds(5), Now);
        }

        DbContextOptions<TaskForgeDbContext> options = new DbContextOptionsBuilder<TaskForgeDbContext>(DatabaseOptions)
            .AddInterceptors(new ConcurrentSaveInterceptor())
            .Options;
        await using TaskForgeDbContext workerContext = new(options);
        await using TaskForgeDbContext recoveryContext = new(options);
        EfCoreJobStore workerStore = new(workerContext);
        Job job = (await workerStore.FindAsync(jobId))!;

        Task<JobAttempt?> start = workerStore.TryStartAttemptAsync(job, Now);
        Task<int> recovery = new EfCoreJobStore(recoveryContext).RecoverExpiredLeasesAsync(Now.AddMinutes(1));
        await Task.WhenAll(start, recovery);

        await using TaskForgeDbContext readContext = new(DatabaseOptions);
        Job persisted = await readContext.Jobs.SingleAsync();
        if (await start is null)
        {
            Assert.Equal(1, await recovery);
            Assert.Equal(JobStatus.Queued, persisted.Status);
            Assert.Empty(await readContext.JobAttempts.ToListAsync());
        }
        else
        {
            Assert.Equal(0, await recovery);
            Assert.Equal(JobStatus.Processing, persisted.Status);
            Assert.Equal(JobAttemptOutcome.Running, (await readContext.JobAttempts.SingleAsync()).Outcome);
        }
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
