using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

using TaskForge.Application.Jobs.Models;
using TaskForge.Domain.Jobs;
using TaskForge.Infrastructure.Persistence;

namespace TaskForge.IntegrationTests.Persistence;

[Collection("SQL Server")]
public sealed class SqlServerJobDistributionTests(SqlServerFixture fixture) : SqlServerTest(fixture)
{
    private static readonly DateTimeOffset Now = new(2026, 9, 23, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Grace = TimeSpan.FromSeconds(30);
    private DbContextOptions<TaskForgeDbContext> DistributionOptions => new DbContextOptionsBuilder<TaskForgeDbContext>()
        .UseSqlServer(ConnectionString, options => options.EnableRetryOnFailure()).Options;

    [Fact]
    public async Task Distribution_persists_matching_ownership_start_times_and_attempt_identity()
    {
        Job job = CreateJob();
        await SeedAsync(job);
        await using TaskForgeDbContext context = new(DistributionOptions);

        JobExecutionAssignment assignment = Assert.IsType<JobExecutionAssignment>(await DistributeAsync(context));

        await using TaskForgeDbContext readContext = new(DistributionOptions);
        Job persisted = await readContext.Jobs.SingleAsync();
        JobAttempt attempt = await readContext.JobAttempts.SingleAsync();
        Assert.Equal(job.Id, assignment.Job.Id);
        Assert.Equal(attempt.Id, assignment.AttemptId);
        Assert.NotEqual(Guid.Empty, assignment.AttemptId);
        Assert.Equal(persisted.Id, attempt.JobId);
        Assert.Equal(JobStatus.Processing, persisted.Status);
        Assert.Equal(JobAttemptOutcome.Running, attempt.Outcome);
        Assert.Equal("worker-01", persisted.OwningWorkerId);
        Assert.Equal(persisted.OwningWorkerId, attempt.WorkerId);
        Assert.Equal(Now, persisted.StartedAtUtc);
        Assert.Equal(persisted.StartedAtUtc, attempt.StartedAtUtc);
        Assert.Equal(Now.AddSeconds(job.TimeoutSeconds), assignment.DeadlineAtUtc);
        Assert.Equal(assignment.DeadlineAtUtc.Add(Grace), persisted.LeaseExpiresAtUtc);
        Assert.Equal(1, attempt.AttemptNumber);
        Assert.Equal(job.Version + 1, persisted.Version);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task Competing_clients_create_one_assignment_per_job_and_retry_with_fresh_state(int jobCount)
    {
        Job[] jobs = Enumerable.Range(0, jobCount).Select(_ => CreateJob()).ToArray();
        await SeedAsync(jobs);
        DbContextOptions<TaskForgeDbContext> options = new DbContextOptionsBuilder<TaskForgeDbContext>(DistributionOptions)
            .AddInterceptors(new ConcurrentSaveInterceptor()).Options;
        await using TaskForgeDbContext first = new(options);
        await using TaskForgeDbContext second = new(options);

        JobExecutionAssignment?[] results = await Task.WhenAll(DistributeAsync(first, "worker-01"), DistributeAsync(second, "worker-02"));

        JobExecutionAssignment[] assignments = results.OfType<JobExecutionAssignment>().ToArray();
        Assert.Equal(jobCount, assignments.Length);
        Assert.Equal(jobCount, assignments.Select(assignment => assignment.Job.Id).Distinct().Count());
        await using TaskForgeDbContext readContext = new(DistributionOptions);
        List<Job> persistedJobs = await readContext.Jobs.ToListAsync();
        List<JobAttempt> attempts = await readContext.JobAttempts.ToListAsync();
        Assert.Equal(jobCount, attempts.Count);
        foreach (Job job in persistedJobs)
        {
            JobAttempt attempt = Assert.Single(attempts, candidate => candidate.JobId == job.Id);
            JobExecutionAssignment assignment = Assert.Single(assignments, candidate => candidate.Job.Id == job.Id);
            Assert.Equal(JobStatus.Processing, job.Status);
            Assert.Equal(JobAttemptOutcome.Running, attempt.Outcome);
            Assert.Equal(job.OwningWorkerId, attempt.WorkerId);
            Assert.Equal(job.StartedAtUtc, attempt.StartedAtUtc);
            Assert.Equal(attempt.Id, assignment.AttemptId);
            Assert.Equal(1, attempt.AttemptNumber);
        }
    }

    [Fact]
    public async Task Application_and_supported_types_are_filtered_before_ordering()
    {
        Job otherApplication = CreateJob(applicationId: "other-app", priority: JobPriority.High);
        Job unsupported = CreateJob(type: "unsupported", priority: JobPriority.High);
        Job supported = CreateJob(type: "second-type");
        await SeedAsync(otherApplication, unsupported, supported);
        await using TaskForgeDbContext context = new(DistributionOptions);
        EfCoreJobStore store = new(context);

        JobExecutionAssignment assignment = Assert.IsType<JobExecutionAssignment>(
            await store.TryDistributeAsync(" TEST-APP ", "worker-01", ["EXAMPLE", "SECOND-TYPE"], Grace, Now));

        Assert.Equal(supported.Id, assignment.Job.Id);
        Assert.Null(await store.TryDistributeAsync("test-app", "worker-01", ["example", "second-type"], Grace, Now));
        Assert.Null(await store.TryDistributeAsync("missing-app", "worker-01", ["example"], Grace, Now));
        Assert.Null(await store.TryDistributeAsync("other-app", "worker-01", [], Grace, Now));
        await using TaskForgeDbContext readContext = new(DistributionOptions);
        Assert.Equal(2, await readContext.Jobs.CountAsync(job => job.Status == JobStatus.Queued));
        Assert.Equal(1, await readContext.JobAttempts.CountAsync());
    }

    [Fact]
    public async Task Priority_due_time_creation_time_and_stable_id_determine_order_and_future_retries_are_excluded()
    {
        Job future = CreateRetryJob(Now.AddSeconds(1), JobPriority.High);
        Job dueHigh = CreateRetryJob(Now, JobPriority.High);
        Job dueEarly = CreateRetryJob(Now.AddMinutes(-3));
        Job dueLate = CreateRetryJob(Now.AddMinutes(-2));
        Job olderCreated = CreateJob(createdAt: Now.AddMinutes(-5));
        Job lowerId = CreateJob(id: Guid.Parse("00000000-0000-0000-0000-000000000001"));
        Job higherId = CreateJob(id: Guid.Parse("00000000-0000-0000-0000-000000000002"));
        await SeedAsync(future, higherId, dueLate, lowerId, olderCreated, dueEarly, dueHigh);
        await using TaskForgeDbContext context = new(DistributionOptions);
        foreach (Job expected in new[] { dueHigh, dueEarly, dueLate, olderCreated, lowerId, higherId })
        {
            JobExecutionAssignment assignment = Assert.IsType<JobExecutionAssignment>(await DistributeAsync(context));
            Assert.Equal(expected.Id, assignment.Job.Id);
            Assert.Null(assignment.Job.NextRetryAtUtc);
        }

        Assert.Null(await DistributeAsync(context));
        await using TaskForgeDbContext readContext = new(DistributionOptions);
        Assert.Equal(6, await readContext.JobAttempts.CountAsync());
        Job remaining = await readContext.Jobs.SingleAsync(job => job.Id == future.Id);
        Assert.Equal(JobStatus.Retrying, remaining.Status);
        Assert.Equal(Now.AddSeconds(1), remaining.NextRetryAtUtc);
        Assert.Null(remaining.OwningWorkerId);
    }

    [Fact]
    public async Task Attempt_numbers_increase_across_failed_retries_and_lease_recovery()
    {
        await SeedAsync(CreateJob());
        await using TaskForgeDbContext context = new(DistributionOptions);
        EfCoreJobStore store = new(context);
        JobExecutionAssignment first = (await DistributeAsync(context))!;
        long version = first.Job.Version;
        first.Attempt.Finish(JobAttemptOutcome.Failed, Now.AddSeconds(1));
        first.Job.Fail("Retryable failure.", Now.AddSeconds(2), Now.AddSeconds(1));
        Assert.True(await store.TryUpdateAsync(first.Job, version, CancellationToken.None, first.Attempt));

        JobExecutionAssignment second = (await DistributeAsync(context, now: Now.AddSeconds(2)))!;
        Assert.Equal(2, second.Attempt.AttemptNumber);
        DateTimeOffset expiredAt = second.Job.LeaseExpiresAtUtc!.Value;
        await using (TaskForgeDbContext recoveryContext = new(DistributionOptions))
        {
            Assert.Equal(1, await new EfCoreJobStore(recoveryContext).RecoverExpiredLeasesAsync(expiredAt));
        }

        JobExecutionAssignment third = (await DistributeAsync(context, "replacement-worker", expiredAt))!;

        Assert.Equal(3, third.Attempt.AttemptNumber);
        Assert.Equal(1, third.Job.RetryCount);
        await using TaskForgeDbContext readContext = new(DistributionOptions);
        List<JobAttempt> attempts = await readContext.JobAttempts.OrderBy(attempt => attempt.AttemptNumber).ToListAsync();
        Assert.Equal([1, 2, 3], attempts.Select(attempt => attempt.AttemptNumber));
        Assert.Equal([JobAttemptOutcome.Failed, JobAttemptOutcome.Abandoned, JobAttemptOutcome.Running], attempts.Select(attempt => attempt.Outcome));
        Assert.Equal(third.AttemptId, attempts[2].Id);
        Assert.Equal(expiredAt, attempts[1].FinishedAtUtc);
    }

    [Fact]
    public async Task Failure_after_database_writes_rolls_back_both_job_and_attempt_and_allows_retry()
    {
        Job original = CreateJob();
        await SeedAsync(original);
        FailAfterSaveInterceptor failure = new();
        DbContextOptions<TaskForgeDbContext> options = new DbContextOptionsBuilder<TaskForgeDbContext>(DistributionOptions)
            .AddInterceptors(failure).Options;
        await using TaskForgeDbContext context = new(options);

        await Assert.ThrowsAsync<InjectedPersistenceException>(() => DistributeAsync(context));

        Assert.True(failure.SawBothWrites);
        Assert.Empty(context.ChangeTracker.Entries());
        await using (TaskForgeDbContext readContext = new(DistributionOptions))
        {
            Job persisted = await readContext.Jobs.SingleAsync();
            Assert.Equal(JobStatus.Queued, persisted.Status);
            Assert.Null(persisted.OwningWorkerId);
            Assert.Null(persisted.StartedAtUtc);
            Assert.Null(persisted.LeaseExpiresAtUtc);
            Assert.Equal(original.Version, persisted.Version);
            Assert.Empty(await readContext.JobAttempts.ToListAsync());
        }

        JobExecutionAssignment assignment = (await DistributeAsync(context))!;
        Assert.Equal(1, assignment.Attempt.AttemptNumber);
        await using TaskForgeDbContext finalContext = new(DistributionOptions);
        Assert.Equal(assignment.AttemptId, (await finalContext.JobAttempts.SingleAsync()).Id);
    }

    [Fact]
    public async Task Repeated_version_conflicts_are_bounded_and_leave_no_assignment()
    {
        Job job = CreateJob();
        await SeedAsync(job);
        ChangeVersionInterceptor conflicts = new(DistributionOptions, job.Id);
        DbContextOptions<TaskForgeDbContext> options = new DbContextOptionsBuilder<TaskForgeDbContext>(DistributionOptions)
            .AddInterceptors(conflicts).Options;
        await using TaskForgeDbContext context = new(options);

        Assert.Null(await DistributeAsync(context));

        Assert.Equal(5, conflicts.SaveCount);
        Assert.Equal(Enumerable.Range(1, 5).Select(value => (long)value), conflicts.ObservedVersions);
        await using TaskForgeDbContext readContext = new(DistributionOptions);
        Job persisted = await readContext.Jobs.SingleAsync();
        Assert.Equal(JobStatus.Queued, persisted.Status);
        Assert.Null(persisted.OwningWorkerId);
        Assert.Empty(await readContext.JobAttempts.ToListAsync());
    }

    [Fact]
    public async Task Cancellation_before_distribution_leaves_job_unassigned()
    {
        await SeedAsync(CreateJob());
        await using TaskForgeDbContext context = new(DistributionOptions);
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new EfCoreJobStore(context)
            .TryDistributeAsync("test-app", "worker-01", ["example"], Grace, Now, cancellation.Token));

        Assert.Equal(JobStatus.Queued, (await context.Jobs.SingleAsync()).Status);
        Assert.Empty(await context.JobAttempts.ToListAsync());
    }

    private static Task<JobExecutionAssignment?> DistributeAsync(TaskForgeDbContext context, string workerId = "worker-01", DateTimeOffset? now = null) =>
        new EfCoreJobStore(context).TryDistributeAsync("test-app", workerId, ["example"], Grace, now ?? Now);

    private async Task SeedAsync(params Job[] jobs)
    {
        await using TaskForgeDbContext context = new(DistributionOptions);
        context.Jobs.AddRange(jobs);
        await context.SaveChangesAsync();
    }

    private static Job CreateJob(string applicationId = "test-app", string type = "example", JobPriority priority = JobPriority.Normal, DateTimeOffset? createdAt = null, Guid? id = null)
    {
        Job job = new(id ?? Guid.NewGuid(), applicationId, type, "{}", priority, 3, 10, createdAt ?? Now.AddMinutes(-2));
        job.Queue(Now.AddMinutes(-1));
        return job;
    }

    private static Job CreateRetryJob(DateTimeOffset dueAt, JobPriority priority = JobPriority.Normal)
    {
        Job job = new(Guid.NewGuid(), "test-app", "example", "{}", priority, 3, 10, Now.AddMinutes(-10));
        job.Queue(Now.AddMinutes(-10));
        job.StartProcessing("previous-worker", Now.AddMinutes(-9), Now.AddMinutes(-10));
        job.Fail("Retryable failure.", dueAt, Now.AddMinutes(-9));
        return job;
    }

    private sealed class InjectedPersistenceException : Exception;

    private sealed class FailAfterSaveInterceptor : SaveChangesInterceptor
    {
        private bool _failed;
        public bool SawBothWrites { get; private set; }

        public override async ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result, CancellationToken cancellationToken = default)
        {
            if (!_failed)
            {
                _failed = true;
                TaskForgeDbContext context = (TaskForgeDbContext)eventData.Context!;
                Job job = await context.Jobs.AsNoTracking().SingleAsync(cancellationToken);
                JobAttempt attempt = await context.JobAttempts.AsNoTracking().SingleAsync(cancellationToken);
                SawBothWrites = job.Status == JobStatus.Processing && attempt.Outcome == JobAttemptOutcome.Running;
                throw new InjectedPersistenceException();
            }

            return result;
        }
    }

    private sealed class ChangeVersionInterceptor(DbContextOptions<TaskForgeDbContext> options, Guid jobId) : SaveChangesInterceptor
    {
        public int SaveCount { get; private set; }
        public List<long> ObservedVersions { get; } = [];

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            SaveCount++;
            ObservedVersions.Add(eventData.Context!.Entry(eventData.Context.ChangeTracker.Entries<Job>().Single().Entity)
                .Property(job => job.Version).OriginalValue);
            await using TaskForgeDbContext competitor = new(options);
            await competitor.Jobs.Where(job => job.Id == jobId)
                .ExecuteUpdateAsync(setters => setters.SetProperty(job => job.Version, job => job.Version + 1), cancellationToken);
            return result;
        }
    }
}
