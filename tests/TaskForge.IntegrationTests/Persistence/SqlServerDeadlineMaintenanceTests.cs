using System.Data;
using System.Data.Common;
using System.Text.Json;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

using TaskForge.Application.Jobs;
using TaskForge.Application.Jobs.Models;
using TaskForge.Application.Workers;
using TaskForge.Domain.Jobs;
using TaskForge.Infrastructure.Persistence;

namespace TaskForge.IntegrationTests.Persistence;

[Collection("SQL Server")]
public sealed class SqlServerDeadlineMaintenanceTests(SqlServerFixture fixture) : SqlServerTest(fixture)
{
    private static readonly JobRetryPolicy RetryPolicy = new(Options.Create(new WorkerOptions { RetryDelaySeconds = 7, MaxRetryDelaySeconds = 20 }));

    [Theory]
    [InlineData(-1, 3, JobStatus.Processing)]
    [InlineData(0, 3, JobStatus.Retrying)]
    [InlineData(1, 3, JobStatus.Retrying)]
    [InlineData(0, 0, JobStatus.DeadLettered)]
    public async Task Sql_deadline_boundary_expires_before_lease_grace_and_is_idempotent(long ticks, int maxRetries, JobStatus expected)
    {
        JobExecutionAssignment assignment = await SeedAsync(maxRetries: maxRetries);
        DateTimeOffset now = assignment.DeadlineAtUtc.AddTicks(ticks);
        Assert.True(now < assignment.Job.LeaseExpiresAtUtc);
        await using TaskForgeDbContext context = new(With(new SqlClock(now)));
        await Store(context).ExpireDueExecutionsAsync();

        await using TaskForgeDbContext read = new(DatabaseOptions);
        Job job = await read.Jobs.SingleAsync();
        JobAttempt attempt = await read.JobAttempts.SingleAsync();
        Assert.Equal(expected, job.Status);
        if (expected == JobStatus.Processing)
        {
            Assert.Equal(0, job.RetryCount);
            Assert.Equal(JobAttemptOutcome.Running, attempt.Outcome);
            Assert.Equal(assignment.Job.Version, job.Version);
        }
        else
        {
            Assert.Equal(1, job.RetryCount);
            Assert.Equal(JobAttemptOutcome.TimedOut, attempt.Outcome);
            Assert.Equal(now, attempt.FinishedAtUtc);
            Assert.Equal(now, job.UpdatedAtUtc);
            Assert.Equal(assignment.Job.Version + 1, job.Version);
            Assert.Equal("Timeout", attempt.ErrorCode);
            Assert.Equal("Job timed out after 30 second(s).", attempt.ErrorMessage);
            Assert.Equal((long)(now - attempt.StartedAtUtc).TotalMilliseconds, attempt.DurationMilliseconds);
            Assert.Null(job.OwningWorkerId);
            Assert.Null(job.LeaseExpiresAtUtc);
            Assert.Equal(expected == JobStatus.Retrying ? now.AddSeconds(7) : (DateTimeOffset?)null, job.NextRetryAtUtc);
        }

        string snapshot = await SnapshotAsync();
        await Store(context).ExpireDueExecutionsAsync();
        Assert.Equal(snapshot, await SnapshotAsync());
    }

    [Fact]
    public async Task Sweep_is_bounded_and_later_poll_drains_remaining_due_work()
    {
        await using TaskForgeDbContext seed = new(DatabaseOptions);
        DateTimeOffset now = await SqlNowAsync(seed);
        for (int index = 0; index < 101; index++)
        {
            AddAssignment(seed, now.AddMinutes(-1));
        }

        JobExecutionAssignment future = AddAssignment(seed, now.AddMinutes(1));
        await seed.SaveChangesAsync();
        await using TaskForgeDbContext context = new(With(new SqlClock(now)));
        await Store(context).ExpireDueExecutionsAsync();
        Assert.Equal(100, await seed.Jobs.CountAsync(job => job.Status == JobStatus.Retrying));
        Assert.Equal(100, await seed.JobAttempts.CountAsync(attempt => attempt.Outcome == JobAttemptOutcome.TimedOut));
        await Store(context).ExpireDueExecutionsAsync();
        Assert.Equal(101, await seed.Jobs.CountAsync(job => job.Status == JobStatus.Retrying));
        Assert.Equal(future.Job.Id, (await seed.Jobs.AsNoTracking().SingleAsync(job => job.Status == JobStatus.Processing)).Id);
    }

    [Fact]
    public async Task Persisted_cancellation_wins_without_consuming_retry_budget()
    {
        JobExecutionAssignment assignment = await SeedAsync();
        await using (TaskForgeDbContext seed = new(DatabaseOptions))
        {
            Job job = await seed.Jobs.SingleAsync();
            job.RequestCancellation(assignment.Attempt.StartedAtUtc);
            await seed.SaveChangesAsync();
        }

        await using TaskForgeDbContext context = new(With(new SqlClock(assignment.DeadlineAtUtc)));
        await Store(context).ExpireDueExecutionsAsync();
        await using TaskForgeDbContext read = new(DatabaseOptions);
        Job cancelled = await read.Jobs.SingleAsync();
        Assert.Equal(JobStatus.Cancelled, cancelled.Status);
        Assert.Equal(0, cancelled.RetryCount);
        Assert.Null(cancelled.OwningWorkerId);
        Assert.Equal(JobAttemptOutcome.Cancelled, (await read.JobAttempts.SingleAsync()).Outcome);
    }

    [Theory]
    [InlineData("complete")]
    [InlineData("fail")]
    [InlineData("cancel")]
    [InlineData("redistribute")]
    public async Task State_changed_after_scan_is_preserved_on_locked_reload(string operation)
    {
        JobExecutionAssignment assignment = await SeedAsync();
        PauseBeforeLock pause = new();
        await using TaskForgeDbContext sweep = new(With(new SqlClock(assignment.DeadlineAtUtc), pause));
        Task expiration = Store(sweep).ExpireDueExecutionsAsync();
        try
        {
            await pause.Entered.Task.WaitAsync(TimeSpan.FromSeconds(15));
            await using TaskForgeDbContext report = new(With(new SqlClock(assignment.DeadlineAtUtc.AddTicks(-1))));
            if (operation == "cancel")
            {
                Assert.Equal(JobCancellationStatus.Accepted, (await Store(report).CancelAsync(assignment.Job.Id)).Status);
            }
            else
            {
                ExecutionReport outcome = operation == "complete" ? new ExecutionReport.Complete("{\"ok\":true}") : new ExecutionReport.Fail("Temporary", "Try again.");
                Assert.Equal(ExecutionResult.Accepted, await Store(report).TransitionAsync(Identity(assignment), outcome));
                if (operation == "redistribute")
                {
                    await using TaskForgeDbContext acquire = new(DatabaseOptions);
                    JobExecutionAssignment next = (await new EfCoreJobStore(acquire).DistributeAtAsync("test-app", "worker", ["external"], TimeSpan.FromHours(1), assignment.DeadlineAtUtc.AddSeconds(10)))!;
                    Assert.NotEqual(assignment.AttemptId, next.AttemptId);
                }
            }

            string snapshot = await SnapshotAsync();
            pause.Release.TrySetResult();
            await expiration;
            Assert.Equal(snapshot, await SnapshotAsync());
        }
        finally
        {
            pause.Release.TrySetResult();
            await expiration;
        }
    }

    [Theory]
    [InlineData("sweep")]
    [InlineData("complete")]
    [InlineData("fail")]
    [InlineData("lease")]
    public async Task Competing_maintenance_and_late_reports_consume_one_retry(string competitor)
    {
        JobExecutionAssignment assignment = await SeedAsync();
        DateTimeOffset now = competitor == "lease" ? assignment.Job.LeaseExpiresAtUtc!.Value : assignment.DeadlineAtUtc;
        PairBeforeLock pair = new();
        await using TaskForgeDbContext first = new(With(new SqlClock(now), pair));
        await using TaskForgeDbContext second = new(With(new SqlClock(now), pair));
        Task other = competitor switch
        {
            "sweep" => Store(second).ExpireDueExecutionsAsync(),
            "lease" => new EfCoreJobStore(second, RetryPolicy).RecoverExpiredLeasesAsync(now),
            "complete" => Store(second).TransitionAsync(Identity(assignment), new ExecutionReport.Complete()),
            _ => Store(second).TransitionAsync(Identity(assignment), new ExecutionReport.Fail("Temporary", "Try again."))
        };
        await Task.WhenAll(Store(first).ExpireDueExecutionsAsync(), other);

        await using TaskForgeDbContext read = new(DatabaseOptions);
        Job job = await read.Jobs.SingleAsync();
        Assert.Equal(JobStatus.Retrying, job.Status);
        Assert.Equal(1, job.RetryCount);
        Assert.Equal(assignment.Job.Version + 1, job.Version);
        Assert.Equal(JobAttemptOutcome.TimedOut, (await read.JobAttempts.SingleAsync()).Outcome);
        string snapshot = await SnapshotAsync();
        Assert.Equal(ExecutionResult.TimedOut, await Store(first).TransitionAsync(Identity(assignment), new ExecutionReport.Complete()));
        Assert.Equal(ExecutionResult.TimedOut, await Store(first).TransitionAsync(Identity(assignment), new ExecutionReport.Fail("Temporary", "Try again.")));
        Assert.Equal(snapshot, await SnapshotAsync());
    }

    [Fact]
    public async Task Failure_between_writes_rolls_back_and_next_sweep_succeeds()
    {
        JobExecutionAssignment assignment = await SeedAsync();
        FailAttemptWrite failure = new();
        string snapshot = await SnapshotAsync();
        await using TaskForgeDbContext context = new(With(new SqlClock(assignment.DeadlineAtUtc), failure));
        await Assert.ThrowsAsync<InjectedFailure>(() => Store(context).ExpireDueExecutionsAsync());
        Assert.Equal(snapshot, await SnapshotAsync());
        await Store(context).ExpireDueExecutionsAsync();
        await using TaskForgeDbContext read = new(DatabaseOptions);
        Assert.Equal(1, (await read.Jobs.SingleAsync()).RetryCount);
        Assert.Equal(JobAttemptOutcome.TimedOut, (await read.JobAttempts.SingleAsync()).Outcome);
    }

    [Fact]
    public async Task Cancellation_interrupts_sweep_without_changing_execution()
    {
        JobExecutionAssignment assignment = await SeedAsync();
        PauseBeforeLock pause = new();
        using CancellationTokenSource stop = new();
        await using TaskForgeDbContext context = new(With(new SqlClock(assignment.DeadlineAtUtc), pause));
        string snapshot = await SnapshotAsync();
        Task expiration = Store(context).ExpireDueExecutionsAsync(stop.Token);
        try
        {
            await pause.Entered.Task.WaitAsync(TimeSpan.FromSeconds(15));
            stop.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => expiration);
            Assert.Equal(snapshot, await SnapshotAsync());
        }
        finally
        {
            stop.Cancel();
            pause.Release.TrySetResult();
        }
    }

    [Fact]
    public async Task Normal_host_expires_without_clients_and_recovers_after_database_failure()
    {
        JobExecutionAssignment assignment = await SeedAsync();
        FailAttemptWrite failure = new();
        await using WebApplicationFactory<Program> factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ConnectionStrings:TaskForge", ConnectionString);
            builder.UseSetting("Worker:PollIntervalMilliseconds", "50");
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<DbContextOptions<TaskForgeDbContext>>();
                services.AddDbContext<TaskForgeDbContext>(options => options.UseSqlServer(ConnectionString)
                    .AddInterceptors(new SqlClock(assignment.DeadlineAtUtc), failure));
            });
        });
        using HttpClient client = factory.CreateClient();
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(15));
        while (true)
        {
            await using TaskForgeDbContext read = new(DatabaseOptions);
            Job job = await read.Jobs.SingleAsync(deadline.Token);
            if (job.Status != JobStatus.Processing)
            {
                Assert.True(failure.Injected);
                Assert.Equal(JobStatus.Retrying, job.Status);
                Assert.Equal(1, job.RetryCount);
                Assert.Equal(assignment.DeadlineAtUtc, (await read.JobAttempts.SingleAsync(deadline.Token)).FinishedAtUtc);
                Assert.True(assignment.DeadlineAtUtc < assignment.Job.LeaseExpiresAtUtc);
                break;
            }

            await Task.Delay(20, deadline.Token);
        }
    }

    private async Task<JobExecutionAssignment> SeedAsync(int maxRetries = 3)
    {
        await using TaskForgeDbContext context = new(DatabaseOptions);
        JobExecutionAssignment assignment = AddAssignment(context, await SqlNowAsync(context), maxRetries);
        await context.SaveChangesAsync();
        return assignment;
    }

    private static JobExecutionAssignment AddAssignment(TaskForgeDbContext context, DateTimeOffset start, int maxRetries = 3)
    {
        Job job = new(Guid.NewGuid(), "test-app", "external", "{}", JobPriority.Normal, maxRetries, 30, start);
        job.Queue(start);
        job.StartProcessing("worker", start.AddHours(1), start);
        JobAttempt attempt = new(Guid.NewGuid(), job.Id, 1, "worker", start);
        context.Jobs.Add(job);
        context.JobAttempts.Add(attempt);
        return new(job, attempt);
    }

    private DbContextOptions<TaskForgeDbContext> With(params IInterceptor[] interceptors) => new DbContextOptionsBuilder<TaskForgeDbContext>()
        .UseSqlServer(ConnectionString, options => options.EnableRetryOnFailure()).AddInterceptors(interceptors).Options;

    private static EfCoreExecutionStore Store(TaskForgeDbContext context) => new(context, RetryPolicy);
    private static ExecutionIdentity Identity(JobExecutionAssignment assignment) => new(assignment.Job.ApplicationId, assignment.Job.Id, assignment.AttemptId, assignment.Attempt.WorkerId);
    private static Task<DateTimeOffset> SqlNowAsync(TaskForgeDbContext context) => context.Database.SqlQuery<DateTimeOffset>($"SELECT TODATETIMEOFFSET(SYSUTCDATETIME(), '+00:00') AS [Value]").SingleAsync();

    private async Task<string> SnapshotAsync()
    {
        await using TaskForgeDbContext context = new(DatabaseOptions);
        return JsonSerializer.Serialize(new { Jobs = await context.Jobs.OrderBy(job => job.Id).ToArrayAsync(), Attempts = await context.JobAttempts.OrderBy(attempt => attempt.AttemptNumber).ToArrayAsync() });
    }

    private sealed class SqlClock(DateTimeOffset now) : DbCommandInterceptor
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

    private sealed class PauseBeforeLock : DbCommandInterceptor
    {
        private bool _paused;
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (!_paused && command.CommandText.Contains("FROM [Jobs] WITH (UPDLOCK, HOLDLOCK)", StringComparison.Ordinal))
            {
                _paused = true;
                Entered.TrySetResult();
                await Release.Task.WaitAsync(TimeSpan.FromSeconds(15), cancellationToken);
            }

            return result;
        }
    }

    private sealed class InjectedFailure : Exception;

    private sealed class PairBeforeLock : DbCommandInterceptor
    {
        private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _arrivals;

        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("FROM [Jobs] WITH (UPDLOCK, HOLDLOCK)", StringComparison.Ordinal))
            {
                if (Interlocked.Increment(ref _arrivals) == 2)
                {
                    _ready.TrySetResult();
                }

                await _ready.Task.WaitAsync(TimeSpan.FromSeconds(15), cancellationToken);
            }

            return result;
        }
    }

    private sealed class FailAttemptWrite : DbCommandInterceptor
    {
        public bool Injected { get; private set; }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (!Injected && command.CommandText.Contains("UPDATE", StringComparison.Ordinal) && command.CommandText.Contains("[JobAttempts]", StringComparison.Ordinal))
            {
                Injected = true;
                throw new InjectedFailure();
            }

            return ValueTask.FromResult(result);
        }
    }
}
