using System.Data.Common;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

using TaskForge.Application.Jobs.Models;
using TaskForge.Domain.Jobs;
using TaskForge.Infrastructure.Persistence;

namespace TaskForge.IntegrationTests.Persistence;

[Collection("SQL Server")]
public sealed class SqlServerAcquisitionTimingTests(SqlServerFixture fixture) : SqlServerTest(fixture)
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Database_lock_wait_does_not_consume_the_assignment_timeout(bool blockSelection)
    {
        Job job = await SeedAsync();
        await using TaskForgeDbContext blocker = new(DatabaseOptions);
        await using var transaction = await blocker.Database.BeginTransactionAsync();
        if (blockSelection)
        {
            await blocker.Jobs.Where(candidate => candidate.Id == job.Id)
                .ExecuteUpdateAsync(setters => setters.SetProperty(candidate => candidate.UpdatedAtUtc, candidate => candidate.UpdatedAtUtc));
        }
        else
        {
            await blocker.Jobs.FromSqlInterpolated($"SELECT * FROM [Jobs] WITH (UPDLOCK, HOLDLOCK) WHERE [Id] = {job.Id}")
                .AsNoTracking().SingleAsync();
        }

        ObserveBlockedRead observer = new(blockSelection);
        await using TaskForgeDbContext acquiring = new(With(observer));
        Task<JobExecutionAssignment?> acquisition = AcquireAsync(acquiring);
        DateTimeOffset releasedAt;
        try
        {
            await observer.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await Task.Delay(TimeSpan.FromMilliseconds(1200));
            Assert.False(acquisition.IsCompleted);
            releasedAt = await SqlNowAsync(blocker);
        }
        finally
        {
            await transaction.RollbackAsync();
        }

        JobExecutionAssignment assignment = Assert.IsType<JobExecutionAssignment>(await acquisition.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.True(assignment.Attempt.StartedAtUtc >= releasedAt);
        await AssertPersistedTimingAsync(assignment);
    }

    [Fact]
    public async Task Transient_failure_retries_with_a_new_sql_start_and_one_persisted_attempt()
    {
        await SeedAsync();
        FailFirstSave failure = new();
        await using TaskForgeDbContext acquiring = new(With(failure));

        JobExecutionAssignment assignment = Assert.IsType<JobExecutionAssignment>(await AcquireAsync(acquiring));

        Assert.Equal(2, failure.SaveCount);
        Assert.True(assignment.Attempt.StartedAtUtc >= failure.FirstDeadline);
        Assert.NotEqual(failure.FirstAttemptId, assignment.AttemptId);
        await AssertPersistedTimingAsync(assignment);
    }

    [Fact]
    public async Task Version_conflict_reselects_with_fresh_sql_time()
    {
        Job job = await SeedAsync();
        ChangeVersionBeforeLock conflict = new(DatabaseOptions, job.Id);
        await using TaskForgeDbContext acquiring = new(With(conflict));

        JobExecutionAssignment assignment = Assert.IsType<JobExecutionAssignment>(await AcquireAsync(acquiring));

        Assert.Equal(2, conflict.LockCount);
        Assert.True(assignment.Attempt.StartedAtUtc >= conflict.ChangedAt);
        Assert.Equal(job.Version + 2, assignment.Job.Version);
        await AssertPersistedTimingAsync(assignment);
    }

    private async Task<Job> SeedAsync()
    {
        await using TaskForgeDbContext context = new(DatabaseOptions);
        DateTimeOffset now = await SqlNowAsync(context);
        Job job = new(Guid.NewGuid(), "test-app", "external", "{}", JobPriority.Normal, 3, 1, now);
        job.Queue(now);
        context.Jobs.Add(job);
        await context.SaveChangesAsync();
        return job;
    }

    private async Task AssertPersistedTimingAsync(JobExecutionAssignment assignment)
    {
        await using TaskForgeDbContext read = new(DatabaseOptions);
        DateTimeOffset returnedAt = await SqlNowAsync(read);
        Job job = await read.Jobs.SingleAsync();
        JobAttempt attempt = await read.JobAttempts.SingleAsync();
        Assert.Equal(JobStatus.Processing, job.Status);
        Assert.Equal(assignment.AttemptId, attempt.Id);
        Assert.Equal(1, attempt.AttemptNumber);
        Assert.Equal(assignment.Attempt.StartedAtUtc, attempt.StartedAtUtc);
        Assert.Equal(attempt.StartedAtUtc, job.StartedAtUtc);
        Assert.Equal(attempt.StartedAtUtc, job.UpdatedAtUtc);
        Assert.Equal(attempt.StartedAtUtc.AddSeconds(job.TimeoutSeconds), assignment.DeadlineAtUtc);
        Assert.Equal(assignment.DeadlineAtUtc.AddSeconds(30), job.LeaseExpiresAtUtc);
        Assert.True(assignment.DeadlineAtUtc > returnedAt);
    }

    private DbContextOptions<TaskForgeDbContext> With(params IInterceptor[] interceptors) => new DbContextOptionsBuilder<TaskForgeDbContext>(DatabaseOptions)
        .UseSqlServer(ConnectionString, sql => sql.EnableRetryOnFailure()).AddInterceptors(interceptors).Options;

    private static Task<JobExecutionAssignment?> AcquireAsync(TaskForgeDbContext context) =>
        new EfCoreJobStore(context).TryDistributeAsync("test-app", "worker", ["external"], TimeSpan.FromSeconds(30));

    private static Task<DateTimeOffset> SqlNowAsync(TaskForgeDbContext context) =>
        context.Database.SqlQuery<DateTimeOffset>($"SELECT TODATETIMEOFFSET(SYSUTCDATETIME(), '+00:00') AS [Value]").SingleAsync();

    private static bool IsAcquisitionLock(DbCommand command) => command.CommandText.Contains("FROM [Jobs] WITH (UPDLOCK, HOLDLOCK)", StringComparison.Ordinal);

    private sealed class ObserveBlockedRead(bool blockSelection) : DbCommandInterceptor
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (blockSelection ? command.CommandText.Contains("ORDER BY", StringComparison.Ordinal) : IsAcquisitionLock(command))
            {
                Entered.TrySetResult();
            }

            return ValueTask.FromResult(result);
        }
    }

    private sealed class FailFirstSave : SaveChangesInterceptor
    {
        public int SaveCount { get; private set; }
        public DateTimeOffset FirstDeadline { get; private set; }
        public Guid FirstAttemptId { get; private set; }

        public override async ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result, CancellationToken cancellationToken = default)
        {
            if (++SaveCount == 1)
            {
                JobAttempt attempt = eventData.Context!.ChangeTracker.Entries<JobAttempt>().Single().Entity;
                FirstDeadline = attempt.StartedAtUtc.AddSeconds(1);
                FirstAttemptId = attempt.Id;
                await Task.Delay(TimeSpan.FromMilliseconds(1200), cancellationToken);
                throw new TimeoutException("Retryable failure before the acquisition commit.");
            }

            return result;
        }
    }

    private sealed class ChangeVersionBeforeLock(DbContextOptions<TaskForgeDbContext> options, Guid jobId) : DbCommandInterceptor
    {
        public int LockCount { get; private set; }
        public DateTimeOffset ChangedAt { get; private set; }

        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (IsAcquisitionLock(command) && ++LockCount == 1)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(1200), cancellationToken);
                await using TaskForgeDbContext competitor = new(options);
                await competitor.Jobs.Where(job => job.Id == jobId)
                    .ExecuteUpdateAsync(setters => setters.SetProperty(job => job.Version, job => job.Version + 1), cancellationToken);
                ChangedAt = await SqlNowAsync(competitor);
            }

            return result;
        }
    }
}
