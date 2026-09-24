using System.Data;
using System.Data.Common;
using System.Text.Json;

using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Options;

using TaskForge.Application.Jobs;
using TaskForge.Application.Jobs.Models;
using TaskForge.Application.Workers;
using TaskForge.Domain.Jobs;
using TaskForge.Infrastructure.Persistence;

namespace TaskForge.IntegrationTests.Persistence;

[Collection("SQL Server")]
public sealed class SqlServerExecutionTransitionTests(SqlServerFixture fixture) : SqlServerTest(fixture)
{
    private DbContextOptions<TaskForgeDbContext> ExecutionOptions => new DbContextOptionsBuilder<TaskForgeDbContext>()
        .UseSqlServer(ConnectionString, options => options.EnableRetryOnFailure()).Options;

    [Fact]
    public async Task Completion_uses_sql_time_and_commits_job_and_attempt_once()
    {
        JobExecutionAssignment assignment = await AssignAsync();
        await using TaskForgeDbContext context = new(ExecutionOptions);
        EfCoreExecutionStore store = Store(context);
        ExecutionIdentity identity = Identity(assignment);
        DateTimeOffset before = await SqlNowAsync(context);

        Assert.Equal(ExecutionResult.Accepted, (await store.FindExecutionAsync(identity)).Result);
        Assert.Equal(ExecutionResult.Accepted, await store.TransitionAsync(identity, new ExecutionReport.Complete("{\"ok\":true}")));

        DateTimeOffset after = await SqlNowAsync(context);
        await using TaskForgeDbContext read = new(ExecutionOptions);
        Job job = await read.Jobs.SingleAsync();
        JobAttempt attempt = await read.JobAttempts.SingleAsync();
        Assert.Equal(JobStatus.Completed, job.Status);
        Assert.Equal(JobAttemptOutcome.Succeeded, attempt.Outcome);
        Assert.Equal(assignment.Job.Version + 1, job.Version);
        Assert.Equal("{\"ok\":true}", job.ResultJson);
        Assert.Null(job.OwningWorkerId);
        Assert.Null(job.LeaseExpiresAtUtc);
        Assert.InRange(attempt.FinishedAtUtc!.Value, before, after);
        Assert.Equal(attempt.FinishedAtUtc, job.CompletedAtUtc);
        Assert.Equal(attempt.FinishedAtUtc, job.UpdatedAtUtc);
        Assert.Equal((long)(attempt.FinishedAtUtc.Value - attempt.StartedAtUtc).TotalMilliseconds, attempt.DurationMilliseconds);
        Assert.Equal(ExecutionResult.Duplicate, await store.TransitionAsync(identity, new ExecutionReport.Complete("{\"ok\":true}")));
        Assert.Equal(ExecutionResult.Conflicting, await store.TransitionAsync(identity, new ExecutionReport.Complete("{\"different\":true}")));
        Assert.Equal(ExecutionResult.Conflicting, await store.TransitionAsync(identity, new ExecutionReport.Fail("Failure", "Changed report.")));
        await read.Entry(job).ReloadAsync();
        await read.Entry(attempt).ReloadAsync();
        Assert.Equal(assignment.Job.Version + 1, job.Version);
        Assert.Equal(job.CompletedAtUtc, attempt.FinishedAtUtc);
        Assert.Null(attempt.ErrorCode);
    }

    [Theory]
    [InlineData("application", ExecutionResult.Missing)]
    [InlineData("worker", ExecutionResult.Conflicting)]
    [InlineData("worker-case", ExecutionResult.Conflicting)]
    [InlineData("job", ExecutionResult.Missing)]
    [InlineData("attempt", ExecutionResult.Missing)]
    [InlineData("pairing", ExecutionResult.Missing)]
    public async Task Invalid_identity_cannot_read_or_mutate_an_execution(string mismatch, ExecutionResult expected)
    {
        JobExecutionAssignment assignment = await AssignAsync();
        JobExecutionAssignment other = await AssignAsync();
        ExecutionIdentity identity = new(
            mismatch == "application" ? "other-app" : "test-app",
            mismatch == "job" ? Guid.NewGuid() : mismatch == "pairing" ? other.Job.Id : assignment.Job.Id,
            mismatch == "attempt" ? Guid.NewGuid() : assignment.AttemptId,
            mismatch == "worker" ? "different-worker" : mismatch == "worker-case" ? "WORKER-01" : "worker-01");
        await using TaskForgeDbContext context = new(ExecutionOptions);
        EfCoreExecutionStore store = Store(context);

        ExecutionLookup lookup = await store.FindExecutionAsync(identity);
        Assert.Equal(expected, lookup.Result);
        Assert.Null(lookup.Assignment);
        Assert.Equal(expected, await store.TransitionAsync(identity, new ExecutionReport.Complete("{}")));

        await AssertRunningAsync(assignment);
        await AssertRunningAsync(other);
    }

    [Theory]
    [InlineData("owner")]
    [InlineData("start")]
    [InlineData("status")]
    [InlineData("lease")]
    public async Task Active_ownership_and_current_job_state_are_required(string mismatch)
    {
        JobExecutionAssignment assignment = await AssignAsync();
        await using TaskForgeDbContext context = new(ExecutionOptions);
        IQueryable<Job> jobs = context.Jobs.Where(job => job.Id == assignment.Job.Id);
        switch (mismatch)
        {
            case "owner":
                await jobs.ExecuteUpdateAsync(setters => setters.SetProperty(job => job.OwningWorkerId, "other-worker"));
                break;
            case "start":
                await jobs.ExecuteUpdateAsync(setters => setters.SetProperty(job => job.StartedAtUtc, assignment.Attempt.StartedAtUtc.AddSeconds(1)));
                break;
            case "status":
                await jobs.ExecuteUpdateAsync(setters => setters.SetProperty(job => job.Status, JobStatus.Queued));
                break;
            default:
                await jobs.ExecuteUpdateAsync(setters => setters.SetProperty(job => job.LeaseExpiresAtUtc, (DateTimeOffset?)null));
                break;
        }

        Assert.Equal(ExecutionResult.Stale, await Store(context).TransitionAsync(Identity(assignment), new ExecutionReport.Complete()));
        Assert.Equal(JobAttemptOutcome.Running, (await context.JobAttempts.SingleAsync()).Outcome);
        Assert.Equal(assignment.Job.Version, (await context.Jobs.SingleAsync()).Version);
    }

    [Fact]
    public async Task Old_attempt_with_same_worker_and_stale_tracked_entities_cannot_mutate_new_assignment()
    {
        JobExecutionAssignment old = await AssignAsync(expired: true);
        await using TaskForgeDbContext staleContext = new(ExecutionOptions);
        Job staleJob = await staleContext.Jobs.SingleAsync();
        JobAttempt staleAttempt = await staleContext.JobAttempts.SingleAsync();
        staleJob.Complete("stale result", DateTimeOffset.UtcNow);
        staleAttempt.Finish(JobAttemptOutcome.Succeeded, DateTimeOffset.UtcNow);

        JobExecutionAssignment current;
        await using (TaskForgeDbContext context = new(ExecutionOptions))
        {
            DateTimeOffset recoveredAt = await SqlNowAsync(context);
            Assert.Equal(1, await new EfCoreJobStore(context).RecoverExpiredLeasesAsync(recoveredAt));
            current = (await new EfCoreJobStore(context).TryDistributeAsync("test-app", "worker-01", ["example"], TimeSpan.FromSeconds(30), recoveredAt.AddSeconds(5)))!;
        }

        Assert.Equal(ExecutionResult.Stale, await Store(staleContext).TransitionAsync(Identity(old), new ExecutionReport.Complete("\"old report\"")));
        Assert.Equal(ExecutionResult.Stale, await Store(staleContext).TransitionAsync(Identity(old), new ExecutionReport.Fail("Failure", "Stale failure.")));
        Assert.Empty(staleContext.ChangeTracker.Entries());
        await AssertRunningAsync(current);
        await using TaskForgeDbContext read = new(ExecutionOptions);
        JobAttempt abandoned = await read.JobAttempts.SingleAsync(attempt => attempt.Id == old.AttemptId);
        Assert.Equal(JobAttemptOutcome.TimedOut, abandoned.Outcome);
        Assert.Equal("Timeout", abandoned.ErrorCode);
    }

    [Theory]
    [InlineData("Failure", 3, JobStatus.Retrying, JobAttemptOutcome.Failed, 1)]
    [InlineData("Failure", 0, JobStatus.DeadLettered, JobAttemptOutcome.Failed, 1)]
    [InlineData("InvalidPayload", 3, JobStatus.DeadLettered, JobAttemptOutcome.PermanentlyFailed, 0)]
    public async Task Failure_uses_server_retry_policy_and_matching_repeats_are_immutable(string errorCode, int maxRetries, JobStatus status, JobAttemptOutcome outcome, int retryCount)
    {
        JobExecutionAssignment assignment = await AssignAsync(maxRetries: maxRetries);
        await using TaskForgeDbContext context = new(ExecutionOptions);
        EfCoreExecutionStore store = Store(context);
        ExecutionReport.Fail report = new(errorCode, "Execution failed.");

        Assert.Equal(ExecutionResult.Accepted, await store.TransitionAsync(Identity(assignment), report));
        Assert.Equal(ExecutionResult.Duplicate, await store.TransitionAsync(Identity(assignment), report));
        Assert.Equal(ExecutionResult.Conflicting, await store.TransitionAsync(Identity(assignment), new ExecutionReport.Fail("Other", "Different failure.")));

        Job job = await context.Jobs.SingleAsync();
        JobAttempt attempt = await context.JobAttempts.SingleAsync();
        Assert.Equal(status, job.Status);
        Assert.Equal(outcome, attempt.Outcome);
        Assert.Equal(retryCount, job.RetryCount);
        Assert.Equal(assignment.Job.Version + 1, job.Version);
        Assert.Equal(attempt.FinishedAtUtc, job.UpdatedAtUtc);
        Assert.Equal(status == JobStatus.Retrying ? attempt.FinishedAtUtc!.Value.AddSeconds(7) : (DateTimeOffset?)null, job.NextRetryAtUtc);
    }

    [Theory]
    [InlineData(-1, ExecutionResult.Accepted, JobAttemptOutcome.Succeeded)]
    [InlineData(0, ExecutionResult.TimedOut, JobAttemptOutcome.TimedOut)]
    [InlineData(1, ExecutionResult.TimedOut, JobAttemptOutcome.TimedOut)]
    public async Task Deadline_boundary_is_decided_by_sql_utc(long ticksFromDeadline, ExecutionResult expected, JobAttemptOutcome outcome)
    {
        JobExecutionAssignment assignment = await AssignAsync();
        DateTimeOffset sqlTime = assignment.DeadlineAtUtc.AddTicks(ticksFromDeadline);
        await using TaskForgeDbContext context = new(With(new FixedSqlClockInterceptor(sqlTime)));

        Assert.Equal(expected, await Store(context).TransitionAsync(Identity(assignment), new ExecutionReport.Complete("{}")));

        await using TaskForgeDbContext read = new(ExecutionOptions);
        JobAttempt attempt = await read.JobAttempts.SingleAsync();
        Job job = await read.Jobs.SingleAsync();
        Assert.Equal(outcome, attempt.Outcome);
        Assert.Equal(sqlTime, attempt.FinishedAtUtc);
        Assert.Equal(sqlTime, job.UpdatedAtUtc);
        if (outcome == JobAttemptOutcome.TimedOut)
        {
            Assert.Equal(JobStatus.Retrying, job.Status);
            Assert.Equal(sqlTime.AddSeconds(7), job.NextRetryAtUtc);
            Assert.Null(job.ResultJson);
        }
    }

    [Fact]
    public async Task Timeout_during_lease_grace_is_persisted_once_and_cannot_be_overwritten()
    {
        JobExecutionAssignment assignment = await AssignAsync(timeoutSeconds: 1, ageSeconds: 2);
        await using TaskForgeDbContext context = new(ExecutionOptions);
        EfCoreExecutionStore store = Store(context);
        Assert.True(await SqlNowAsync(context) < assignment.Job.LeaseExpiresAtUtc);

        Assert.Equal(ExecutionResult.TimedOut, (await store.FindExecutionAsync(Identity(assignment))).Result);
        Assert.Equal(ExecutionResult.TimedOut, await store.TransitionAsync(Identity(assignment), new ExecutionReport.Timeout()));
        Assert.Equal(ExecutionResult.TimedOut, await store.TransitionAsync(Identity(assignment), new ExecutionReport.Complete("\"late\"")));

        Job job = await context.Jobs.SingleAsync();
        JobAttempt attempt = await context.JobAttempts.SingleAsync();
        Assert.Equal(1, job.RetryCount);
        Assert.Equal(assignment.Job.Version + 1, job.Version);
        Assert.Equal(JobAttemptOutcome.TimedOut, attempt.Outcome);
        Assert.Null(job.ResultJson);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Requested_cancellation_wins_over_reports_and_timeout(bool expired)
    {
        JobExecutionAssignment assignment = await AssignAsync(expired: expired);
        await using TaskForgeDbContext context = new(ExecutionOptions);
        Job job = await context.Jobs.SingleAsync();
        job.RequestCancellation(await SqlNowAsync(context));
        await context.SaveChangesAsync();
        long requestedVersion = job.Version;
        EfCoreExecutionStore store = Store(context);

        Assert.Equal(ExecutionResult.Cancelled, await store.TransitionAsync(Identity(assignment), new ExecutionReport.Complete("\"ignored\"")));
        Assert.Equal(ExecutionResult.Cancelled, await store.TransitionAsync(Identity(assignment), new ExecutionReport.Cancel()));

        Job persisted = await context.Jobs.SingleAsync();
        JobAttempt attempt = await context.JobAttempts.SingleAsync();
        Assert.Equal(JobStatus.Cancelled, persisted.Status);
        Assert.Equal(JobAttemptOutcome.Cancelled, attempt.Outcome);
        Assert.Equal(requestedVersion + 1, persisted.Version);
        Assert.Equal(0, persisted.RetryCount);
        Assert.Equal(persisted.UpdatedAtUtc, attempt.FinishedAtUtc);
        Assert.Null(persisted.ResultJson);
    }

    [Fact]
    public async Task Premature_timeout_and_unrequested_cancellation_cannot_select_terminal_status()
    {
        JobExecutionAssignment assignment = await AssignAsync();
        await using TaskForgeDbContext context = new(ExecutionOptions);
        EfCoreExecutionStore store = Store(context);

        Assert.Equal(ExecutionResult.Conflicting, await store.TransitionAsync(Identity(assignment), new ExecutionReport.Timeout()));
        Assert.Equal(ExecutionResult.Conflicting, await store.TransitionAsync(Identity(assignment), new ExecutionReport.Cancel()));

        await AssertRunningAsync(assignment);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task Concurrent_reports_commit_exactly_one_transition(bool competingFailure, bool conflictingSuccess)
    {
        JobExecutionAssignment assignment = await AssignAsync();
        PairReadsInterceptor barrier = new();
        await using TaskForgeDbContext first = new(With(barrier));
        await using TaskForgeDbContext second = new(With(barrier));
        ExecutionReport firstReport = new ExecutionReport.Complete("{}"), secondReport = competingFailure
            ? new ExecutionReport.Fail("Failure", "Execution failed.")
            : conflictingSuccess ? new ExecutionReport.Complete("[]") : firstReport;

        ExecutionResult[] results = await Task.WhenAll(
            Store(first).TransitionAsync(Identity(assignment), firstReport),
            Store(second).TransitionAsync(Identity(assignment), secondReport));

        Assert.Single(results, result => result == ExecutionResult.Accepted);
        Assert.Single(results, result => result == (competingFailure || conflictingSuccess ? ExecutionResult.Conflicting : ExecutionResult.Duplicate));
        await using TaskForgeDbContext read = new(ExecutionOptions);
        Job job = await read.Jobs.SingleAsync();
        JobAttempt attempt = await read.JobAttempts.SingleAsync();
        Assert.Equal(assignment.Job.Version + 1, job.Version);
        Assert.Equal(job.UpdatedAtUtc, attempt.FinishedAtUtc);
        Assert.Equal(job.Status == JobStatus.Completed ? JobAttemptOutcome.Succeeded : JobAttemptOutcome.Failed, attempt.Outcome);
        Assert.Null(job.OwningWorkerId);
        Assert.Equal(results[0] == ExecutionResult.Accepted ? "{}" : conflictingSuccess ? "[]" : competingFailure ? null : "{}", job.ResultJson);
    }

    [Theory]
    [InlineData(null, "null")]
    [InlineData(" { \"value\" : \"a\" } ", "{\"value\":\"\\u0061\"}")]
    public async Task Duplicate_after_original_deadline_preserves_entire_job_and_history(string? first, string repeated)
    {
        JobExecutionAssignment assignment = await AssignAsync();
        await using TaskForgeDbContext context = new(ExecutionOptions);
        Assert.Equal(ExecutionResult.Accepted, await Store(context).TransitionAsync(Identity(assignment), new ExecutionReport.Complete(first)));
        string jobSnapshot = JsonSerializer.Serialize(await context.Jobs.AsNoTracking().SingleAsync());
        string historySnapshot = JsonSerializer.Serialize(await context.JobAttempts.AsNoTracking().ToArrayAsync());

        await using TaskForgeDbContext late = new(With(new FixedSqlClockInterceptor(assignment.DeadlineAtUtc.AddHours(1))));
        Assert.Equal(ExecutionResult.Duplicate, await Store(late).TransitionAsync(Identity(assignment), new ExecutionReport.Complete(repeated)));
        Assert.Equal(ExecutionResult.Conflicting, await Store(late).TransitionAsync(Identity(assignment), new ExecutionReport.Complete("false")));

        Assert.Equal(jobSnapshot, JsonSerializer.Serialize(await context.Jobs.AsNoTracking().SingleAsync()));
        Assert.Equal(historySnapshot, JsonSerializer.Serialize(await context.JobAttempts.AsNoTracking().ToArrayAsync()));
    }

    [Fact]
    public async Task Report_waiting_for_job_lock_uses_time_after_lock_acquisition()
    {
        JobExecutionAssignment assignment = await AssignAsync(timeoutSeconds: 3);
        await using TaskForgeDbContext blocker = new(DatabaseOptions);
        await using var transaction = await blocker.Database.BeginTransactionAsync();
        await blocker.Database.ExecuteSqlInterpolatedAsync($"UPDATE [Jobs] SET [Version] = [Version] WHERE [Id] = {assignment.Job.Id}");
        PauseCommandInterceptor pause = new(pauseWrite: false);
        await using TaskForgeDbContext reporting = new(With(pause));
        Task<ExecutionResult> report = Store(reporting).TransitionAsync(Identity(assignment), new ExecutionReport.Complete("\"late\""));
        await pause.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        pause.Release.TrySetResult();
        try
        {
            await WaitPastDeadlineAsync(assignment.DeadlineAtUtc);
        }
        finally
        {
            await transaction.RollbackAsync();
        }

        Assert.Equal(ExecutionResult.TimedOut, await report.WaitAsync(TimeSpan.FromSeconds(20)));
        await AssertTimedOutAsync(assignment);
    }

    [Fact]
    public async Task Delayed_conditional_write_rechecks_deadline_and_reloads_before_timeout()
    {
        JobExecutionAssignment assignment = await AssignAsync(timeoutSeconds: 3);
        PauseCommandInterceptor pause = new(pauseWrite: true);
        await using TaskForgeDbContext context = new(With(pause));
        Task<ExecutionResult> report = Store(context).TransitionAsync(Identity(assignment), new ExecutionReport.Complete("\"late\""));
        await pause.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        try
        {
            await WaitPastDeadlineAsync(assignment.DeadlineAtUtc);
        }
        finally
        {
            pause.Release.TrySetResult();
        }

        Assert.Equal(ExecutionResult.TimedOut, await report.WaitAsync(TimeSpan.FromSeconds(20)));
        Assert.Equal(2, pause.WriteCount);
        await AssertTimedOutAsync(assignment);
    }

    [Theory]
    [InlineData(false, "complete")]
    [InlineData(true, "complete")]
    [InlineData(false, "failure")]
    [InlineData(true, "failure")]
    [InlineData(false, "permanent")]
    [InlineData(true, "permanent")]
    public async Task Failure_between_or_after_writes_rolls_back_full_transaction(bool afterAttemptWrite, string operation)
    {
        JobExecutionAssignment assignment = await AssignAsync();
        FailAttemptWriteInterceptor failure = new(afterAttemptWrite);
        await using TaskForgeDbContext context = new(With(failure));
        ExecutionReport report = operation == "complete" ? new ExecutionReport.Complete("{}")
            : new ExecutionReport.Fail(operation == "permanent" ? "InvalidPayload" : "Failure", "Failed.");

        await Assert.ThrowsAsync<InjectedPersistenceException>(() => Store(context).TransitionAsync(Identity(assignment), report));

        Assert.True(failure.Injected);
        Assert.Empty(context.ChangeTracker.Entries());
        await AssertRunningAsync(assignment);
        Assert.Equal(ExecutionResult.Accepted, await Store(context).TransitionAsync(Identity(assignment), report));
    }

    [Fact]
    public async Task Failure_schedule_uses_conditional_write_time_instead_of_initial_read_time()
    {
        JobExecutionAssignment assignment = await AssignAsync();
        AdvancingSqlClockInterceptor clock = new(assignment.Attempt.StartedAtUtc);
        await using TaskForgeDbContext context = new(With(clock));

        Assert.Equal(ExecutionResult.Accepted, await Store(context).TransitionAsync(Identity(assignment), new ExecutionReport.Fail("Unknown", "Failed.")));

        Job job = await context.Jobs.SingleAsync();
        JobAttempt attempt = await context.JobAttempts.SingleAsync();
        Assert.Equal(3, clock.ClockReads);
        Assert.Equal(assignment.Attempt.StartedAtUtc.AddSeconds(3), attempt.FinishedAtUtc);
        Assert.Equal(attempt.FinishedAtUtc, job.UpdatedAtUtc);
        Assert.Equal(attempt.FinishedAtUtc!.Value.AddSeconds(7), job.NextRetryAtUtc);
    }

    [Fact]
    public async Task Repeated_failures_use_capped_delays_then_exhaust_server_budget()
    {
        JobExecutionAssignment assignment = await AssignAsync(maxRetries: 3);
        DateTimeOffset now = assignment.Attempt.StartedAtUtc.AddSeconds(1);
        int[] delays = [7, 14, 20];
        for (int failure = 0; failure < 4; failure++)
        {
            await using TaskForgeDbContext context = new(With(new FixedSqlClockInterceptor(now)));
            Assert.Equal(ExecutionResult.Accepted, await Store(context).TransitionAsync(Identity(assignment), new ExecutionReport.Fail("Unknown", "Failed.")));
            Job job = await context.Jobs.AsNoTracking().SingleAsync();
            JobAttempt attempt = await context.JobAttempts.AsNoTracking().SingleAsync(candidate => candidate.Id == assignment.AttemptId);
            Assert.Equal(failure + 1, job.RetryCount);
            Assert.Equal(JobAttemptOutcome.Failed, attempt.Outcome);
            Assert.Equal(now, attempt.FinishedAtUtc);
            Assert.Equal(now, job.UpdatedAtUtc);
            if (failure < 3)
            {
                Assert.Equal(JobStatus.Retrying, job.Status);
                Assert.Equal(now.AddSeconds(delays[failure]), job.NextRetryAtUtc);
                now = job.NextRetryAtUtc!.Value;
                assignment = (await new EfCoreJobStore(context).TryDistributeAsync("test-app", "worker-01", ["example"], TimeSpan.FromSeconds(30), now))!;
                Assert.NotNull(assignment);
                now = now.AddSeconds(1);
            }
            else
            {
                Assert.Equal(JobStatus.DeadLettered, job.Status);
                Assert.Null(job.NextRetryAtUtc);
                Assert.Equal(ExecutionResult.Duplicate, await Store(context).TransitionAsync(Identity(assignment), new ExecutionReport.Fail(" unknown ", " Failed. ")));
                Assert.Equal(4, (await context.Jobs.AsNoTracking().SingleAsync()).RetryCount);
            }
        }
    }

    [Fact]
    public async Task Historical_failure_duplicate_after_deadline_preserves_current_attempt()
    {
        JobExecutionAssignment old = await AssignAsync();
        await using TaskForgeDbContext context = new(ExecutionOptions);
        Assert.Equal(ExecutionResult.Accepted, await Store(context).TransitionAsync(Identity(old), new ExecutionReport.Fail("Unknown", "Failed.")));
        Job job = await context.Jobs.AsNoTracking().SingleAsync();
        JobExecutionAssignment current = (await new EfCoreJobStore(context).TryDistributeAsync("test-app", "new-worker", ["example"], TimeSpan.FromSeconds(30), job.NextRetryAtUtc!.Value))!;
        Assert.NotNull(current);
        string before = JsonSerializer.Serialize(await context.Jobs.AsNoTracking().SingleAsync());
        string history = JsonSerializer.Serialize(await context.JobAttempts.AsNoTracking().OrderBy(attempt => attempt.AttemptNumber).ToArrayAsync());
        await using TaskForgeDbContext late = new(With(new FixedSqlClockInterceptor(old.DeadlineAtUtc.AddHours(1))));

        Assert.Equal(ExecutionResult.Duplicate, await Store(late).TransitionAsync(Identity(old), new ExecutionReport.Fail(" UNKNOWN ", " Failed. ")));
        Assert.Equal(ExecutionResult.Conflicting, await Store(late).TransitionAsync(Identity(old), new ExecutionReport.Fail("unknown", "Changed.")));
        Assert.Equal(ExecutionResult.Conflicting, await Store(late).TransitionAsync(Identity(old), new ExecutionReport.Complete()));

        Assert.Equal(before, JsonSerializer.Serialize(await context.Jobs.AsNoTracking().SingleAsync()));
        Assert.Equal(history, JsonSerializer.Serialize(await context.JobAttempts.AsNoTracking().OrderBy(attempt => attempt.AttemptNumber).ToArrayAsync()));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Concurrent_failures_consume_one_retry(bool conflicting)
    {
        JobExecutionAssignment assignment = await AssignAsync();
        PairReadsInterceptor barrier = new();
        await using TaskForgeDbContext first = new(With(barrier));
        await using TaskForgeDbContext second = new(With(barrier));

        ExecutionResult[] results = await Task.WhenAll(
            Store(first).TransitionAsync(Identity(assignment), new ExecutionReport.Fail("Failure", "Failed.")),
            Store(second).TransitionAsync(Identity(assignment), new ExecutionReport.Fail(" FAILURE ", conflicting ? "Different." : " Failed. ")));

        Assert.Single(results, result => result == ExecutionResult.Accepted);
        Assert.Single(results, result => result == (conflicting ? ExecutionResult.Conflicting : ExecutionResult.Duplicate));
        await using TaskForgeDbContext read = new(ExecutionOptions);
        Job job = await read.Jobs.SingleAsync();
        JobAttempt attempt = await read.JobAttempts.SingleAsync();
        Assert.Equal(1, job.RetryCount);
        Assert.Equal(assignment.Job.Version + 1, job.Version);
        Assert.Equal(JobAttemptOutcome.Failed, attempt.Outcome);
        Assert.Equal(job.UpdatedAtUtc, attempt.FinishedAtUtc);
        Assert.Equal(results[0] == ExecutionResult.Accepted ? "Failed." : conflicting ? "Different." : "Failed.", attempt.ErrorMessage);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Concurrency_conflict_rolls_back_and_rereads_cancellation_state(bool rejectVersionInSql)
    {
        JobExecutionAssignment assignment = await AssignAsync();
        ConflictInterceptor conflict = new(async () =>
        {
            await using TaskForgeDbContext competitor = new(ExecutionOptions);
            Job job = await competitor.Jobs.SingleAsync();
            job.RequestCancellation(await SqlNowAsync(competitor));
            await competitor.SaveChangesAsync();
        }, rejectVersionInSql ? assignment.Job.Version : null);
        await using TaskForgeDbContext context = new(With(conflict));

        Assert.Equal(ExecutionResult.Cancelled, await Store(context).TransitionAsync(Identity(assignment), new ExecutionReport.Complete("\"ignored\"")));

        Assert.Equal(2, conflict.ReadCount);
        await using TaskForgeDbContext read = new(ExecutionOptions);
        Assert.Equal(JobStatus.Cancelled, (await read.Jobs.SingleAsync()).Status);
        Assert.Equal(JobAttemptOutcome.Cancelled, (await read.JobAttempts.SingleAsync()).Outcome);
    }

    [Fact]
    public async Task Repeated_conflicts_are_bounded_and_leave_both_records_unchanged()
    {
        JobExecutionAssignment assignment = await AssignAsync();
        ConflictInterceptor conflict = new();
        await using TaskForgeDbContext context = new(With(conflict));

        Assert.Equal(ExecutionResult.Conflicting, await Store(context).TransitionAsync(Identity(assignment), new ExecutionReport.Complete("{}")));

        Assert.Equal(5, conflict.ReadCount);
        Assert.Equal(5, conflict.WriteCount);
        await AssertRunningAsync(assignment);
    }

    private DbContextOptions<TaskForgeDbContext> With(IInterceptor interceptor) =>
        new DbContextOptionsBuilder<TaskForgeDbContext>(ExecutionOptions).AddInterceptors(interceptor).Options;

    private static EfCoreExecutionStore Store(TaskForgeDbContext context) =>
        new(context, new JobRetryPolicy(Options.Create(new WorkerOptions { RetryDelaySeconds = 7, MaxRetryDelaySeconds = 20 })));

    private static ExecutionIdentity Identity(JobExecutionAssignment assignment) =>
        new(" TEST-APP ", assignment.Job.Id, assignment.AttemptId, assignment.Attempt.WorkerId);

    private async Task<JobExecutionAssignment> AssignAsync(int timeoutSeconds = 300, bool expired = false, int ageSeconds = 0, int maxRetries = 3)
    {
        await using TaskForgeDbContext context = new(ExecutionOptions);
        DateTimeOffset start = (await SqlNowAsync(context)).AddSeconds(expired ? -400 : -ageSeconds);
        Job job = new(Guid.NewGuid(), "test-app", "example", "{}", JobPriority.Normal, maxRetries, timeoutSeconds, start);
        job.Queue(start);
        await new EfCoreJobStore(context).AddAsync(job);
        return (await new EfCoreJobStore(context).TryDistributeAsync("test-app", "worker-01", ["example"], TimeSpan.FromSeconds(30), start))!;
    }

    private static Task<DateTimeOffset> SqlNowAsync(TaskForgeDbContext context) =>
        context.Database.SqlQuery<DateTimeOffset>($"SELECT TODATETIMEOFFSET(SYSUTCDATETIME(), '+00:00') AS [Value]").SingleAsync();

    private async Task WaitPastDeadlineAsync(DateTimeOffset deadline)
    {
        await using TaskForgeDbContext clock = new(ExecutionOptions);
        DateTimeOffset now = await SqlNowAsync(clock);
        Assert.True(now < deadline, "The report must enter the database wait before its deadline.");
        await Task.Delay(deadline - now + TimeSpan.FromMilliseconds(100));
        Assert.True(await SqlNowAsync(clock) >= deadline);
    }

    private async Task AssertRunningAsync(JobExecutionAssignment assignment)
    {
        await using TaskForgeDbContext read = new(ExecutionOptions);
        Job job = await read.Jobs.SingleAsync(candidate => candidate.Id == assignment.Job.Id);
        JobAttempt attempt = await read.JobAttempts.SingleAsync(candidate => candidate.Id == assignment.AttemptId);
        Assert.Equal(JobStatus.Processing, job.Status);
        Assert.Equal(assignment.Job.Version, job.Version);
        Assert.Equal(assignment.Attempt.WorkerId, job.OwningWorkerId);
        Assert.Null(job.ResultJson);
        Assert.Null(job.CompletedAtUtc);
        Assert.Equal(JobAttemptOutcome.Running, attempt.Outcome);
        Assert.Null(attempt.FinishedAtUtc);
    }

    private async Task AssertTimedOutAsync(JobExecutionAssignment assignment)
    {
        await using TaskForgeDbContext read = new(ExecutionOptions);
        Job job = await read.Jobs.SingleAsync();
        JobAttempt attempt = await read.JobAttempts.SingleAsync();
        Assert.Equal(JobStatus.Retrying, job.Status);
        Assert.Equal(JobAttemptOutcome.TimedOut, attempt.Outcome);
        Assert.True(attempt.FinishedAtUtc >= assignment.DeadlineAtUtc);
        Assert.Equal(attempt.FinishedAtUtc, job.UpdatedAtUtc);
        Assert.Equal(attempt.FinishedAtUtc!.Value.AddSeconds(7), job.NextRetryAtUtc);
        Assert.Null(job.ResultJson);
        Assert.Equal(assignment.Job.Version + 1, job.Version);
    }

    private static bool IsJobRead(DbCommand command) => command.CommandText.Contains("FROM [Jobs] WITH (UPDLOCK, HOLDLOCK)", StringComparison.Ordinal);

    private static bool IsGuardedWrite(DbCommand command) => command.CommandText.Contains("DECLARE @transitionUtc", StringComparison.Ordinal);

    private sealed class AdvancingSqlClockInterceptor(DateTimeOffset start) : DbCommandInterceptor
    {
        public int ClockReads { get; private set; }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("SYSUTCDATETIME()", StringComparison.Ordinal))
            {
                command.CommandText = command.CommandText.Replace("SYSUTCDATETIME()", "@testSqlUtc", StringComparison.Ordinal);
                command.Parameters.Add(new SqlParameter("@testSqlUtc", SqlDbType.DateTime2) { Value = start.AddSeconds(++ClockReads).UtcDateTime, Scale = 7 });
            }

            return ValueTask.FromResult(result);
        }
    }

    private sealed class FixedSqlClockInterceptor(DateTimeOffset now) : DbCommandInterceptor
    {
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("SYSUTCDATETIME()", StringComparison.Ordinal))
            {
                command.CommandText = command.CommandText.Replace("SYSUTCDATETIME()", "@testSqlUtc", StringComparison.Ordinal);
                command.Parameters.Add(new SqlParameter("@testSqlUtc", SqlDbType.DateTime2) { Value = now.UtcDateTime, Scale = 7 });
            }

            return ValueTask.FromResult(result);
        }
    }

    private sealed class PairReadsInterceptor : DbCommandInterceptor
    {
        private readonly TaskCompletionSource _bothReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _arrivals;

        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (IsJobRead(command))
            {
                if (Interlocked.Increment(ref _arrivals) == 2)
                {
                    _bothReady.TrySetResult();
                }

                await _bothReady.Task.WaitAsync(TimeSpan.FromSeconds(20), cancellationToken);
            }

            return result;
        }
    }

    private sealed class PauseCommandInterceptor(bool pauseWrite) : DbCommandInterceptor
    {
        private bool _paused;
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int WriteCount { get; private set; }

        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (IsGuardedWrite(command))
            {
                WriteCount++;
            }

            if (!_paused && (pauseWrite ? IsGuardedWrite(command) : IsJobRead(command)))
            {
                _paused = true;
                Entered.TrySetResult();
                await Release.Task.WaitAsync(TimeSpan.FromSeconds(20), cancellationToken);
            }

            return result;
        }
    }

    private sealed class InjectedPersistenceException : Exception;

    private sealed class FailAttemptWriteInterceptor(bool afterWrite) : DbCommandInterceptor
    {
        public bool Injected { get; private set; }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (!afterWrite)
            {
                Inject(command);
            }

            return ValueTask.FromResult(result);
        }

        public override ValueTask<int> NonQueryExecutedAsync(DbCommand command, CommandExecutedEventData eventData, int result, CancellationToken cancellationToken = default)
        {
            if (afterWrite)
            {
                Inject(command);
            }

            return ValueTask.FromResult(result);
        }

        private void Inject(DbCommand command)
        {
            if (!Injected && command.CommandText.Contains("UPDATE", StringComparison.Ordinal) && command.CommandText.Contains("[JobAttempts]", StringComparison.Ordinal))
            {
                Injected = true;
                throw new InjectedPersistenceException();
            }
        }
    }

    private sealed class ConflictInterceptor(Func<Task>? changeAfterConflict = null, long? rejectedVersion = null) : DbCommandInterceptor
    {
        public int ReadCount { get; private set; }
        public int WriteCount { get; private set; }

        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (IsJobRead(command))
            {
                ReadCount++;
                if (ReadCount == 2 && changeAfterConflict is not null)
                {
                    await changeAfterConflict();
                }
            }

            if (IsGuardedWrite(command) && (++WriteCount == 1 || changeAfterConflict is null))
            {
                if (rejectedVersion is not null)
                {
                    DbParameter expectedVersion = command.Parameters.Cast<DbParameter>()
                        .Single(parameter => parameter.DbType == DbType.Int64 && Equals(parameter.Value, rejectedVersion.Value));
                    expectedVersion.Value = rejectedVersion.Value - 1;
                    return result;
                }

                throw new DbUpdateConcurrencyException("Injected version conflict.");
            }

            return result;
        }
    }
}
