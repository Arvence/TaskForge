using System.Data.Common;
using System.Text.Json;

using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using TaskForge.Application.Jobs;
using TaskForge.Application.Jobs.Models;
using TaskForge.Application.Workers;
using TaskForge.Domain.Jobs;
using TaskForge.Infrastructure.Persistence;

namespace TaskForge.IntegrationTests.Persistence;

[Collection("SQL Server")]
public sealed class SqlServerLeaseRecoveryTests(SqlServerFixture fixture) : SqlServerTest(fixture)
{
    private static readonly DateTimeOffset Now = new(2026, 9, 24, 12, 0, 0, TimeSpan.Zero);
    private static readonly JobRetryPolicy RetryPolicy = new(Options.Create(new WorkerOptions { RetryDelaySeconds = 7, MaxRetryDelaySeconds = 10 }));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Repeated_client_loss_exhausts_budget_and_preserves_monotonic_history(bool legacy)
    {
        Job job = await SeedAsync(maxRetries: 2);
        DateTimeOffset start = Now;
        for (int number = 1; number <= 3; number++)
        {
            await using TaskForgeDbContext context = new(DatabaseOptions);
            EfCoreJobStore store = Store(context);
            if (legacy)
            {
                Assert.NotNull(await store.TryAcquireNextAsync("lost-worker", TimeSpan.FromSeconds(5), start));
            }
            else
            {
                Assert.NotNull(await store.TryDistributeAsync("test-app", "lost-worker", ["external"], TimeSpan.FromSeconds(5), start));
            }

            DateTimeOffset expired = start.AddSeconds(35);
            Assert.Equal(1, await store.RecoverExpiredLeasesAsync(expired));
            string snapshot = await SnapshotAsync();
            Assert.Equal(0, await store.RecoverExpiredLeasesAsync(expired.AddSeconds(1)));
            Assert.Equal(snapshot, await SnapshotAsync());
            Job persisted = (await store.FindAsync(job.Id))!;
            Assert.Equal(number, persisted.RetryCount);
            Assert.Null(persisted.OwningWorkerId);
            Assert.Null(persisted.LeaseExpiresAtUtc);
            JobAttempt attempt = (await store.GetAttemptsAsync(job.Id))!.Attempts.Last();
            Assert.Equal(number, attempt.AttemptNumber);
            Assert.Equal(start, attempt.StartedAtUtc);
            Assert.Equal("lost-worker", attempt.WorkerId);
            Assert.Equal(legacy ? "LegacyLeaseRecovery" : "Timeout", attempt.ErrorCode);
            Assert.Equal(legacy ? JobAttemptOutcome.Abandoned : JobAttemptOutcome.TimedOut, attempt.Outcome);
            Assert.Equal(expired, attempt.FinishedAtUtc);
            if (number < 3)
            {
                Assert.Equal(JobStatus.Retrying, persisted.Status);
                Assert.Equal(expired.AddSeconds(number == 1 ? 7 : 10), persisted.NextRetryAtUtc);
                start = persisted.NextRetryAtUtc!.Value;
            }
            else
            {
                Assert.Equal(JobStatus.DeadLettered, persisted.Status);
                Assert.Null(persisted.NextRetryAtUtc);
                Assert.Null(await store.TryDistributeAsync("test-app", "replacement", ["external"], TimeSpan.FromSeconds(5), expired.AddDays(1)));
            }
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cancellation_wins_without_consuming_retry_budget(bool legacy)
    {
        Job job = await SeedAsync();
        await using TaskForgeDbContext context = new(DatabaseOptions);
        EfCoreJobStore store = Store(context);
        if (legacy)
        {
            await store.TryAcquireNextAsync("lost-worker", TimeSpan.FromSeconds(5), Now);
        }
        else
        {
            await store.TryDistributeAsync("test-app", "lost-worker", ["external"], TimeSpan.FromSeconds(5), Now);
        }

        context.ChangeTracker.Clear();
        Job processing = await context.Jobs.SingleAsync();
        processing.RequestCancellation(Now.AddSeconds(1));
        await context.SaveChangesAsync();
        Assert.Equal(1, await store.RecoverExpiredLeasesAsync(Now.AddMinutes(1)));
        Job cancelled = (await store.FindAsync(job.Id))!;
        Assert.Equal(JobStatus.Cancelled, cancelled.Status);
        Assert.Equal(0, cancelled.RetryCount);
        Assert.Null(cancelled.NextRetryAtUtc);
        JobAttempt attempt = Assert.Single((await store.GetAttemptsAsync(job.Id))!.Attempts);
        Assert.Equal(JobAttemptOutcome.Cancelled, attempt.Outcome);
        Assert.Equal(legacy ? "LegacyLeaseRecovery" : "CancellationRequested", attempt.ErrorCode);
        Assert.Equal(0, await store.RecoverExpiredLeasesAsync(Now.AddMinutes(2)));
    }

    [Theory]
    [InlineData(JobAttemptOutcome.Abandoned, false)]
    [InlineData(JobAttemptOutcome.TimedOut, false)]
    [InlineData(JobAttemptOutcome.Abandoned, true)]
    [InlineData(JobAttemptOutcome.Cancelled, true)]
    public async Task Old_split_finish_reconciles_job_once_without_rewriting_history(JobAttemptOutcome outcome, bool cancel)
    {
        Job job = await SeedAsync();
        await using TaskForgeDbContext context = new(DatabaseOptions);
        EfCoreJobStore store = Store(context);
        Job acquired = (await store.TryAcquireNextAsync("legacy-worker", TimeSpan.FromSeconds(5), Now))!;
        JobAttempt attempt = (await store.TryStartAttemptAsync(acquired, Now.AddSeconds(1)))!;
        attempt.Finish(outcome, Now.AddSeconds(31), "OriginalError", "Original history.");
        await store.FinishAttemptAsync(attempt);
        if (cancel)
        {
            long version = acquired.Version;
            acquired.RequestCancellation(Now.AddSeconds(32));
            await store.TryUpdateAsync(acquired, version);
        }

        string history = JsonSerializer.Serialize(await store.GetAttemptsAsync(job.Id));
        Assert.Equal(1, await store.RecoverExpiredLeasesAsync(Now.AddMinutes(1)));
        Assert.Equal(history, JsonSerializer.Serialize(await store.GetAttemptsAsync(job.Id)));
        Assert.Equal(0, await store.RecoverExpiredLeasesAsync(Now.AddMinutes(2)));
        Job recovered = (await store.FindAsync(job.Id))!;
        Assert.Equal(cancel ? JobStatus.Cancelled : JobStatus.Retrying, recovered.Status);
        Assert.Equal(cancel ? 0 : 1, recovered.RetryCount);
    }

    [Fact]
    public async Task Already_committed_timeout_is_unchanged_after_lease_expiry()
    {
        DateTimeOffset start = DateTimeOffset.UtcNow.AddMinutes(-2);
        await SeedAsync(start: start);
        await using TaskForgeDbContext context = new(DatabaseOptions);
        JobExecutionAssignment assignment = (await Store(context).TryDistributeAsync("test-app", "worker", ["external"], TimeSpan.FromSeconds(5), start))!;
        EfCoreExecutionStore execution = new(context, RetryPolicy);
        Assert.Equal(ExecutionResult.TimedOut, await execution.TransitionAsync(new("test-app", assignment.Job.Id, assignment.AttemptId, "worker"), new ExecutionReport.Timeout()));
        string snapshot = await SnapshotAsync();
        Assert.Equal(0, await Store(context).RecoverExpiredLeasesAsync(DateTimeOffset.UtcNow));
        Assert.Equal(snapshot, await SnapshotAsync());
    }

    [Fact]
    public async Task Timeout_report_and_lease_recovery_consume_one_retry_together()
    {
        DateTimeOffset start = DateTimeOffset.UtcNow.AddMinutes(-2);
        await SeedAsync(start: start);
        JobExecutionAssignment assignment;
        await using (TaskForgeDbContext setup = new(DatabaseOptions))
        {
            assignment = (await Store(setup).TryDistributeAsync("test-app", "worker", ["external"], TimeSpan.FromSeconds(5), start))!;
        }

        await using TaskForgeDbContext recovery = new(DatabaseOptions);
        await using TaskForgeDbContext reporting = new(DatabaseOptions);
        Task<int> recover = Store(recovery).RecoverExpiredLeasesAsync(DateTimeOffset.UtcNow);
        Task<ExecutionResult> timeout = new EfCoreExecutionStore(reporting, RetryPolicy)
            .TransitionAsync(new("test-app", assignment.Job.Id, assignment.AttemptId, "worker"), new ExecutionReport.Timeout());
        await Task.WhenAll(recover, timeout);
        Assert.Equal(ExecutionResult.TimedOut, await timeout);
        Assert.InRange(await recover, 0, 1);
        await using TaskForgeDbContext read = new(DatabaseOptions);
        Job job = await read.Jobs.SingleAsync();
        JobAttempt attempt = await read.JobAttempts.SingleAsync();
        Assert.Equal(1, job.RetryCount);
        Assert.Equal(JobStatus.Retrying, job.Status);
        Assert.Equal(JobAttemptOutcome.TimedOut, attempt.Outcome);
        Assert.Equal(job.UpdatedAtUtc, attempt.FinishedAtUtc);
        Assert.Equal(job.UpdatedAtUtc.AddSeconds(7), job.NextRetryAtUtc);
    }

    [Fact]
    public async Task Active_lease_and_its_history_are_unchanged()
    {
        await SeedAsync();
        await using TaskForgeDbContext context = new(DatabaseOptions);
        await Store(context).TryDistributeAsync("test-app", "worker", ["external"], TimeSpan.FromSeconds(5), Now);
        string snapshot = await SnapshotAsync();
        Assert.Equal(0, await Store(context).RecoverExpiredLeasesAsync(Now.AddSeconds(34)));
        Assert.Equal(snapshot, await SnapshotAsync());
    }

    [Fact]
    public async Task Legacy_delayed_start_is_recovered_only_after_its_deadline()
    {
        await SeedAsync();
        await using TaskForgeDbContext context = new(DatabaseOptions);
        EfCoreJobStore store = Store(context);
        Job job = (await store.TryAcquireNextAsync("legacy-worker", TimeSpan.FromSeconds(5), Now))!;
        Assert.NotNull(await store.TryStartAttemptAsync(job, Now.AddSeconds(20)));
        string snapshot = await SnapshotAsync();
        Assert.Equal(0, await store.RecoverExpiredLeasesAsync(Now.AddSeconds(35)));
        Assert.Equal(snapshot, await SnapshotAsync());
        Assert.Equal(1, await store.RecoverExpiredLeasesAsync(Now.AddSeconds(50)));
        Assert.Equal(JobAttemptOutcome.TimedOut, (await context.JobAttempts.SingleAsync()).Outcome);
        Assert.Equal(1, (await context.Jobs.SingleAsync()).RetryCount);
    }

    [Theory]
    [InlineData("worker")]
    [InlineData("start")]
    [InlineData("lease")]
    [InlineData("short-lease")]
    [InlineData("different-worker")]
    [InlineData("newer-attempt")]
    [InlineData("succeeded")]
    public async Task Ambiguous_ownership_is_logged_and_never_redistributed(string inconsistency)
    {
        await SeedAsync();
        await using TaskForgeDbContext context = new(DatabaseOptions);
        JobExecutionAssignment assignment = (await Store(context).TryDistributeAsync("test-app", "worker", ["external"], TimeSpan.FromSeconds(5), Now))!;
        context.ChangeTracker.Clear();
        switch (inconsistency)
        {
            case "worker":
                await context.Jobs.ExecuteUpdateAsync(setters => setters.SetProperty(job => job.OwningWorkerId, (string?)null));
                break;
            case "start":
                await context.Jobs.ExecuteUpdateAsync(setters => setters.SetProperty(job => job.StartedAtUtc, (DateTimeOffset?)null));
                break;
            case "lease":
                await context.Jobs.ExecuteUpdateAsync(setters => setters.SetProperty(job => job.LeaseExpiresAtUtc, (DateTimeOffset?)null));
                break;
            case "short-lease":
                await context.Jobs.ExecuteUpdateAsync(setters => setters.SetProperty(job => job.LeaseExpiresAtUtc, Now.AddSeconds(1)));
                break;
            case "different-worker":
                await context.Jobs.ExecuteUpdateAsync(setters => setters.SetProperty(job => job.OwningWorkerId, "WORKER"));
                break;
            case "newer-attempt":
                context.JobAttempts.Add(new(Guid.NewGuid(), assignment.Job.Id, 2, "worker", Now.AddSeconds(1)));
                await context.SaveChangesAsync();
                break;
            case "succeeded":
                assignment.Attempt.Finish(JobAttemptOutcome.Succeeded, Now.AddSeconds(1));
                await Store(context).FinishAttemptAsync(assignment.Attempt);
                break;
        }

        string snapshot = await SnapshotAsync();
        RecoveryLogger logger = new();
        EfCoreJobStore store = new(context, RetryPolicy, logger);
        Assert.Equal(0, await store.RecoverExpiredLeasesAsync(Now.AddMinutes(1)));
        Assert.Contains(logger.Messages, message => message.Contains("inconsistent ownership"));
        Assert.Null(await store.TryDistributeAsync("test-app", "replacement", ["external"], TimeSpan.FromSeconds(5), Now.AddMinutes(2)));
        Assert.Equal(snapshot, await SnapshotAsync());
    }

    [Fact]
    public async Task Concurrent_sweeps_create_one_legacy_entry_and_consume_one_retry()
    {
        await SeedAsync();
        await using (TaskForgeDbContext setup = new(DatabaseOptions))
        {
            await Store(setup).TryAcquireNextAsync("legacy-worker", TimeSpan.FromSeconds(5), Now);
        }

        await using TaskForgeDbContext first = new(DatabaseOptions);
        await using TaskForgeDbContext second = new(DatabaseOptions);
        int[] counts = await Task.WhenAll(Store(first).RecoverExpiredLeasesAsync(Now.AddMinutes(1)), Store(second).RecoverExpiredLeasesAsync(Now.AddMinutes(1)));
        Assert.Equal(1, counts.Sum());
        await using TaskForgeDbContext read = new(DatabaseOptions);
        Assert.Equal(1, (await read.Jobs.SingleAsync()).RetryCount);
        Assert.Equal("LegacyLeaseRecovery", (await read.JobAttempts.SingleAsync()).ErrorCode);
    }

    [Fact]
    public async Task Scan_does_not_overwrite_newer_ownership_acquired_before_locked_reload()
    {
        await SeedAsync();
        await using (TaskForgeDbContext setup = new(DatabaseOptions))
        {
            await Store(setup).TryDistributeAsync("test-app", "same-worker", ["external"], TimeSpan.FromSeconds(5), Now);
        }

        string? expected = null;
        BeforeRecoveryLock interceptor = new(async () =>
        {
            await using TaskForgeDbContext competitor = new(DatabaseOptions);
            Assert.Equal(1, await Store(competitor).RecoverExpiredLeasesAsync(Now.AddMinutes(1)));
            Assert.NotNull(await Store(competitor).TryDistributeAsync("test-app", "same-worker", ["external"], TimeSpan.FromSeconds(5), Now.AddMinutes(1).AddSeconds(7)));
            expected = await SnapshotAsync();
        });
        await using TaskForgeDbContext context = new(new DbContextOptionsBuilder<TaskForgeDbContext>(DatabaseOptions).AddInterceptors(interceptor).Options);
        Assert.Equal(0, await Store(context).RecoverExpiredLeasesAsync(Now.AddMinutes(1)));
        Assert.NotNull(expected);
        Assert.Equal(expected, await SnapshotAsync());
    }

    [Fact]
    public async Task Finished_attempt_committed_after_scan_is_preserved_and_ambiguous_job_is_held()
    {
        await SeedAsync();
        JobExecutionAssignment assignment;
        await using (TaskForgeDbContext setup = new(DatabaseOptions))
        {
            assignment = (await Store(setup).TryDistributeAsync("test-app", "worker", ["external"], TimeSpan.FromSeconds(5), Now))!;
        }

        string? expected = null;
        BeforeRecoveryLock interceptor = new(async () =>
        {
            await using TaskForgeDbContext competitor = new(DatabaseOptions);
            assignment.Attempt.Finish(JobAttemptOutcome.Succeeded, Now.AddSeconds(1));
            await Store(competitor).FinishAttemptAsync(assignment.Attempt);
            expected = await SnapshotAsync();
        });
        await using TaskForgeDbContext context = new(new DbContextOptionsBuilder<TaskForgeDbContext>(DatabaseOptions).AddInterceptors(interceptor).Options);
        Assert.Equal(0, await Store(context).RecoverExpiredLeasesAsync(Now.AddMinutes(1)));
        Assert.NotNull(expected);
        Assert.Equal(expected, await SnapshotAsync());
    }

    [Fact]
    public async Task Cancellation_committed_after_scan_wins_on_locked_reload()
    {
        await SeedAsync();
        await using (TaskForgeDbContext setup = new(DatabaseOptions))
        {
            await Store(setup).TryDistributeAsync("test-app", "worker", ["external"], TimeSpan.FromSeconds(5), Now);
        }

        BeforeRecoveryLock interceptor = new(async () =>
        {
            await using TaskForgeDbContext competitor = new(DatabaseOptions);
            Job job = await competitor.Jobs.SingleAsync();
            job.RequestCancellation(Now.AddSeconds(1));
            await competitor.SaveChangesAsync();
        });
        await using TaskForgeDbContext context = new(new DbContextOptionsBuilder<TaskForgeDbContext>(DatabaseOptions).AddInterceptors(interceptor).Options);
        Assert.Equal(1, await Store(context).RecoverExpiredLeasesAsync(Now.AddMinutes(1)));
        Job cancelled = await context.Jobs.SingleAsync();
        Assert.Equal(JobStatus.Cancelled, cancelled.Status);
        Assert.Equal(0, cancelled.RetryCount);
        Assert.Equal(JobAttemptOutcome.Cancelled, (await context.JobAttempts.SingleAsync()).Outcome);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Failure_after_writes_rolls_back_recovery_and_history_together(bool legacy)
    {
        await SeedAsync();
        await using (TaskForgeDbContext setup = new(DatabaseOptions))
        {
            if (legacy)
            {
                await Store(setup).TryAcquireNextAsync("worker", TimeSpan.FromSeconds(5), Now);
            }
            else
            {
                await Store(setup).TryDistributeAsync("test-app", "worker", ["external"], TimeSpan.FromSeconds(5), Now);
            }
        }

        string snapshot = await SnapshotAsync();
        FailAfterSave interceptor = new();
        await using TaskForgeDbContext failing = new(new DbContextOptionsBuilder<TaskForgeDbContext>(DatabaseOptions).AddInterceptors(interceptor).Options);
        await Assert.ThrowsAsync<InjectedPersistenceException>(() => Store(failing).RecoverExpiredLeasesAsync(Now.AddMinutes(1)));
        Assert.True(interceptor.SawBothWrites);
        Assert.Equal(snapshot, await SnapshotAsync());
        await using TaskForgeDbContext retry = new(DatabaseOptions);
        Assert.Equal(1, await Store(retry).RecoverExpiredLeasesAsync(Now.AddMinutes(1)));
        Assert.Equal(1, (await retry.Jobs.SingleAsync()).RetryCount);
        Assert.Single(await retry.JobAttempts.ToListAsync());
    }

    [Fact]
    public async Task Restart_after_deadline_and_grace_recovers_with_zero_local_workers()
    {
        DateTimeOffset start = DateTimeOffset.UtcNow;
        await SeedAsync(start: start);
        await using (TaskForgeDbContext setup = new(DatabaseOptions))
        {
            await Store(setup).TryDistributeAsync("test-app", "lost-client", ["external"], TimeSpan.FromSeconds(5), start);
        }

        await using (WebApplicationFactory<Program> first = CreateServer(start))
        {
            using HttpClient client = first.CreateClient();
            Assert.True((await client.GetAsync("/api/health")).IsSuccessStatusCode);
        }

        await using (WebApplicationFactory<Program> restarted = CreateServer(start.AddMinutes(1)))
        {
            using HttpClient client = restarted.CreateClient();
            using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(30));
            while (true)
            {
                await using TaskForgeDbContext read = new(DatabaseOptions);
                Job persisted = await read.Jobs.SingleAsync(deadline.Token);
                if (persisted.Status != JobStatus.Processing)
                {
                    Assert.Equal(JobStatus.Retrying, persisted.Status);
                    Assert.Equal(1, persisted.RetryCount);
                    Assert.Equal(JobAttemptOutcome.TimedOut, (await read.JobAttempts.SingleAsync(deadline.Token)).Outcome);
                    break;
                }

                await Task.Delay(20, deadline.Token);
            }
        }

        string snapshot = await SnapshotAsync();
        await using (WebApplicationFactory<Program> restartedAgain = CreateServer(start.AddMinutes(2)))
        {
            using HttpClient client = restartedAgain.CreateClient();
            Assert.True((await client.GetAsync("/api/health")).IsSuccessStatusCode);
        }

        Assert.Equal(snapshot, await SnapshotAsync());
    }

    private WebApplicationFactory<Program> CreateServer(DateTimeOffset now) => new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
    {
        builder.UseSetting("ConnectionStrings:TaskForge", ConnectionString);
        builder.ConfigureServices(services => services.AddSingleton<TimeProvider>(new FixedTimeProvider(now)));
    });

    private async Task<Job> SeedAsync(int maxRetries = 3, DateTimeOffset? start = null)
    {
        Job job = new(Guid.NewGuid(), "test-app", "external", "{}", JobPriority.Normal, maxRetries, 30, start ?? Now);
        job.Queue(start ?? Now);
        await using TaskForgeDbContext context = new(DatabaseOptions);
        await Store(context).AddAsync(job);
        return job;
    }

    private async Task<string> SnapshotAsync()
    {
        await using TaskForgeDbContext context = new(DatabaseOptions);
        return JsonSerializer.Serialize(new { Jobs = await context.Jobs.OrderBy(job => job.Id).ToListAsync(), Attempts = await context.JobAttempts.OrderBy(attempt => attempt.AttemptNumber).ToListAsync() });
    }

    private static EfCoreJobStore Store(TaskForgeDbContext context) => new(context, RetryPolicy);

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class RecoveryLogger : ILogger<EfCoreJobStore>
    {
        public List<string> Messages { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) => Messages.Add(formatter(state, exception));
    }

    private sealed class BeforeRecoveryLock(Func<Task> action) : DbCommandInterceptor
    {
        private bool _invoked;

        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (!_invoked && command.CommandText.Contains("FROM [Jobs] WITH (UPDLOCK, HOLDLOCK)"))
            {
                _invoked = true;
                await action();
            }

            return result;
        }
    }

    private sealed class InjectedPersistenceException : Exception;

    private sealed class FailAfterSave : SaveChangesInterceptor
    {
        public bool SawBothWrites { get; private set; }

        public override async ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result, CancellationToken cancellationToken = default)
        {
            TaskForgeDbContext context = (TaskForgeDbContext)eventData.Context!;
            Job job = await context.Jobs.AsNoTracking().SingleAsync(cancellationToken);
            JobAttempt attempt = await context.JobAttempts.AsNoTracking().SingleAsync(cancellationToken);
            SawBothWrites = job.Status == JobStatus.Retrying && job.RetryCount == 1 && attempt.Outcome != JobAttemptOutcome.Running;
            throw new InjectedPersistenceException();
        }
    }
}
