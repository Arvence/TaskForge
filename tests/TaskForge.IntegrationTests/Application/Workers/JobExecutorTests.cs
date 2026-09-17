using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using TaskForge.Application.Abstractions.Execution;
using TaskForge.Application.Jobs;
using TaskForge.Application.Jobs.Models;
using TaskForge.Application.Workers;
using TaskForge.Domain.Jobs;
using TaskForge.Infrastructure.Persistence;

namespace TaskForge.IntegrationTests.Application.Workers;

[Collection("SQL Server")]
public sealed class JobExecutorTests(SqlServerFixture fixture) : SqlServerTest(fixture)
{
    private static readonly DateTimeOffset Now =
        new(2026, 7, 28, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Successful_job_is_processed_to_completion()
    {
        DbContextOptions<TaskForgeDbContext> databaseOptions = DatabaseOptions;
        Job job = CreateQueuedJob(
            "successful",
            """{"value":1}""",
            maxRetries: 0);
        await AddJobAsync(databaseOptions, job);
        await using TaskForgeDbContext workerContext = new(databaseOptions);
        EfCoreJobStore store = new(workerContext);
        AdjustableTimeProvider clock = new(Now);
        JobExecutor executor = CreateExecutor(
            store,
            [new SuccessfulJobHandler(() => clock.SetUtcNow(Now.AddMilliseconds(250)))],
            timeProvider: clock);
        Guid? currentJobId = null;

        bool processed = await executor.ProcessNextAsync(
            "worker-01",
            id => currentJobId = id,
            CancellationToken.None,
            CancellationToken.None);

        Job? persistedJob = await store.FindAsync(job.Id);
        Assert.True(processed);
        Assert.Null(currentJobId);
        Assert.NotNull(persistedJob);
        Assert.Equal(JobStatus.Completed, persistedJob.Status);
        Assert.Null(persistedJob.OwningWorkerId);
        JobAttempt attempt = Assert.Single(await ReadAttemptsAsync(job.Id));
        Assert.Equal(1, attempt.AttemptNumber);
        Assert.Equal("worker-01", attempt.WorkerId);
        Assert.Equal(Now, attempt.StartedAtUtc);
        Assert.Equal(Now.AddMilliseconds(250), attempt.FinishedAtUtc);
        Assert.Equal(250, attempt.DurationMilliseconds);
        Assert.Equal(JobAttemptOutcome.Succeeded, attempt.Outcome);
        Assert.Null(attempt.ErrorMessage);
    }

    [Fact]
    public async Task Unsupported_job_is_dead_lettered()
    {
        DbContextOptions<TaskForgeDbContext> databaseOptions = DatabaseOptions;
        Job job = CreateQueuedJob(
            "unknown",
            """{"value":1}""",
            maxRetries: 3);
        await AddJobAsync(databaseOptions, job);
        await using TaskForgeDbContext workerContext = new(databaseOptions);
        EfCoreJobStore store = new(workerContext);
        JobExecutor executor = CreateExecutor(store, []);

        await executor.ProcessNextAsync(
            "worker-01",
            _ => { },
            CancellationToken.None,
            CancellationToken.None);

        Job? persistedJob = await store.FindAsync(job.Id);
        Assert.NotNull(persistedJob);
        Assert.Equal(JobStatus.DeadLettered, persistedJob.Status);
        Assert.Contains("No handler", persistedJob.LastError);
        Assert.Empty(await ReadAttemptsAsync(job.Id));
    }

    [Fact]
    public async Task Failed_job_uses_retry_policy()
    {
        DbContextOptions<TaskForgeDbContext> databaseOptions = DatabaseOptions;
        Job job = CreateQueuedJob(
            "failing",
            """{"value":1}""",
            maxRetries: 1);
        await AddJobAsync(databaseOptions, job);
        await using TaskForgeDbContext workerContext = new(databaseOptions);
        EfCoreJobStore store = new(workerContext);
        JobExecutor executor = CreateExecutor(
            store,
            [new FailingJobHandler()]);

        await executor.ProcessNextAsync(
            "worker-01",
            _ => { },
            CancellationToken.None,
            CancellationToken.None);

        Job? persistedJob = await store.FindAsync(job.Id);
        Assert.NotNull(persistedJob);
        Assert.Equal(JobStatus.Retrying, persistedJob.Status);
        Assert.Equal(1, persistedJob.RetryCount);
        Assert.Equal(Now.AddSeconds(5), persistedJob.NextRetryAtUtc);
        Assert.Contains("Expected failure", persistedJob.LastError);
        JobAttempt attempt = Assert.Single(await ReadAttemptsAsync(job.Id));
        Assert.Equal(JobAttemptOutcome.Failed, attempt.Outcome);
        Assert.Equal("InvalidOperationException", attempt.ErrorCode);
        Assert.Equal("Expected failure.", attempt.ErrorMessage);
        Assert.NotNull(attempt.FinishedAtUtc);
    }

    [Fact]
    public async Task Non_retryable_failure_is_dead_lettered_immediately()
    {
        DbContextOptions<TaskForgeDbContext> databaseOptions = DatabaseOptions;
        Job job = CreateQueuedJob(
            "permanent-failure",
            """{"value":1}""",
            maxRetries: 3);
        await AddJobAsync(databaseOptions, job);
        await using TaskForgeDbContext workerContext = new(databaseOptions);
        EfCoreJobStore store = new(workerContext);
        JobExecutor executor = CreateExecutor(
            store,
            [new NonRetryableJobHandler()]);

        await executor.ProcessNextAsync(
            "worker-01",
            _ => { },
            CancellationToken.None,
            CancellationToken.None);

        Job? persistedJob = await store.FindAsync(job.Id);
        Assert.NotNull(persistedJob);
        Assert.Equal(JobStatus.DeadLettered, persistedJob.Status);
        Assert.Equal(0, persistedJob.RetryCount);
        Assert.Null(persistedJob.NextRetryAtUtc);
        Assert.Contains("Permanent failure", persistedJob.LastError);
        JobAttempt attempt = Assert.Single(await ReadAttemptsAsync(job.Id));
        Assert.Equal(JobAttemptOutcome.PermanentlyFailed, attempt.Outcome);
        Assert.Equal("NonRetryableJobException", attempt.ErrorCode);
        Assert.Equal("Permanent failure.", attempt.ErrorMessage);
    }

    [Fact]
    public async Task Timeout_failure_is_scheduled_for_retry()
    {
        DbContextOptions<TaskForgeDbContext> databaseOptions = DatabaseOptions;
        Job job = CreateQueuedJob(
            "timeout",
            """{"value":1}""",
            maxRetries: 1);
        await AddJobAsync(databaseOptions, job);
        await using TaskForgeDbContext workerContext = new(databaseOptions);
        EfCoreJobStore store = new(workerContext);
        JobExecutor executor = CreateExecutor(
            store,
            [new TimeoutJobHandler()]);

        await executor.ProcessNextAsync(
            "worker-01",
            _ => { },
            CancellationToken.None,
            CancellationToken.None);

        Job? persistedJob = await store.FindAsync(job.Id);
        Assert.NotNull(persistedJob);
        Assert.Equal(JobStatus.Retrying, persistedJob.Status);
        Assert.Equal(Now.AddSeconds(5), persistedJob.NextRetryAtUtc);
        Assert.Contains("timeout", persistedJob.LastError, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Repeated_failures_use_capped_exponential_backoff()
    {
        DbContextOptions<TaskForgeDbContext> databaseOptions = DatabaseOptions;
        Job job = CreateQueuedJob(
            "failing",
            """{"value":1}""",
            maxRetries: 3);
        await AddJobAsync(databaseOptions, job);
        AdjustableTimeProvider clock = new(Now);
        WorkerOptions options = new()
        {
            Count = 1,
            PollIntervalMilliseconds = 50,
            RetryDelaySeconds = 5,
            MaxRetryDelaySeconds = 12,
            LeaseGraceSeconds = 30
        };

        Job? firstFailure = await ProcessFailingJobAsync(databaseOptions, job.Id, clock, options);
        Assert.NotNull(firstFailure);
        Assert.Equal(Now.AddSeconds(5), firstFailure.NextRetryAtUtc);

        clock.SetUtcNow(Now.AddSeconds(5));
        Job? secondFailure = await ProcessFailingJobAsync(databaseOptions, job.Id, clock, options);
        Assert.NotNull(secondFailure);
        Assert.Equal(Now.AddSeconds(15), secondFailure.NextRetryAtUtc);

        clock.SetUtcNow(Now.AddSeconds(15));
        Job? thirdFailure = await ProcessFailingJobAsync(databaseOptions, job.Id, clock, options);
        Assert.NotNull(thirdFailure);
        Assert.Equal(3, thirdFailure.RetryCount);
        Assert.Equal(Now.AddSeconds(27), thirdFailure.NextRetryAtUtc);

        clock.SetUtcNow(Now.AddSeconds(27));
        Job? finalFailure = await ProcessFailingJobAsync(databaseOptions, job.Id, clock, options);
        Assert.Equal(JobStatus.DeadLettered, finalFailure!.Status);
        IReadOnlyList<JobAttempt> attempts = await ReadAttemptsAsync(job.Id);
        Assert.Equal([1, 2, 3, 4], attempts.Select(attempt => attempt.AttemptNumber));
        Assert.All(attempts, attempt => Assert.Equal(JobAttemptOutcome.Failed, attempt.Outcome));
    }

    [Fact]
    public async Task Running_job_can_be_cancelled()
    {
        string connectionString = ConnectionString;
        DbContextOptions<TaskForgeDbContext> databaseOptions =
            new DbContextOptionsBuilder<TaskForgeDbContext>()
                .UseSqlServer(connectionString)
                .Options;
        Job job = CreateQueuedJob(
            "blocking",
            """{"value":1}""",
            maxRetries: 0);
        await AddJobAsync(databaseOptions, job);
        JobCancellationRegistry cancellationRegistry = new();
        BlockingJobHandler handler = new();

        await using TaskForgeDbContext workerContext = new(databaseOptions);
        EfCoreJobStore workerStore = new(workerContext);
        JobExecutor executor = CreateExecutor(
            workerStore,
            [handler],
            cancellationRegistry);
        Task<bool> execution = executor.ProcessNextAsync(
            "worker-01",
            _ => { },
            CancellationToken.None,
            CancellationToken.None);
        await handler.Started.WaitAsync(TimeSpan.FromSeconds(30));

        JobAttempt running = Assert.Single(await ReadAttemptsAsync(job.Id));
        Assert.Equal(JobAttemptOutcome.Running, running.Outcome);
        Assert.Null(running.FinishedAtUtc);
        Assert.Null(running.DurationMilliseconds);

        await using TaskForgeDbContext apiContext = new(databaseOptions);
        JobCancellationService cancellationService = new(
            new EfCoreJobStore(apiContext),
            cancellationRegistry,
            new FixedTimeProvider(Now.AddSeconds(1)));
        JobCancellationResult cancellation = await cancellationService.RequestAsync(
            job.Id);

        Assert.True(await execution.WaitAsync(TimeSpan.FromSeconds(30)));
        await using TaskForgeDbContext readContext = new(databaseOptions);
        Job? persistedJob = await new EfCoreJobStore(readContext).FindAsync(job.Id);
        Assert.Equal(JobCancellationStatus.Accepted, cancellation.Status);
        Assert.NotNull(persistedJob);
        Assert.Equal(JobStatus.Cancelled, persistedJob.Status);
        Assert.True(persistedJob.CancellationRequested);
        Assert.True(handler.WasCancelled);
        Assert.Equal(JobAttemptOutcome.Cancelled, Assert.Single(await ReadAttemptsAsync(job.Id)).Outcome);
    }

    [Fact]
    public async Task Cancellation_wins_when_completion_tries_to_save_a_stale_version()
    {
        Job job = CreateQueuedJob("successful", "{}", maxRetries: 0);
        await AddJobAsync(DatabaseOptions, job);
        CompletionSaveInterceptor completion = new();
        DbContextOptions<TaskForgeDbContext> workerOptions = new DbContextOptionsBuilder<TaskForgeDbContext>(DatabaseOptions)
            .AddInterceptors(completion)
            .Options;
        JobCancellationRegistry cancellationRegistry = new();
        await using TaskForgeDbContext workerContext = new(workerOptions);
        JobExecutor executor = CreateExecutor(new EfCoreJobStore(workerContext), [new SuccessfulJobHandler()], cancellationRegistry);
        Task<bool> execution = executor.ProcessNextAsync("worker-01", _ => { }, CancellationToken.None, CancellationToken.None);

        try
        {
            await completion.Ready.WaitAsync(TimeSpan.FromSeconds(30));
            await using TaskForgeDbContext apiContext = new(DatabaseOptions);
            JobCancellationService cancellationService = new(new EfCoreJobStore(apiContext), cancellationRegistry, new FixedTimeProvider(Now));
            JobCancellationResult result = await cancellationService.RequestAsync(job.Id);
            Assert.Equal(JobCancellationStatus.Accepted, result.Status);
        }
        finally
        {
            completion.Release();
            await execution.WaitAsync(TimeSpan.FromSeconds(30));
        }

        Assert.True(await execution);
        Assert.True(completion.ConcurrencyConflictObserved);
        await using TaskForgeDbContext readContext = new(DatabaseOptions);
        Job persisted = await readContext.Jobs.AsNoTracking().SingleAsync();
        Assert.Equal(JobStatus.Cancelled, persisted.Status);
        Assert.True(persisted.CancellationRequested);
        Assert.Null(persisted.CompletedAtUtc);
        Assert.Null(persisted.OwningWorkerId);
        Assert.Null(persisted.LeaseExpiresAtUtc);
        Assert.Equal(job.Version + 4, persisted.Version);
        JobAttempt attempt = Assert.Single(await ReadAttemptsAsync(job.Id));
        Assert.Equal(JobAttemptOutcome.Cancelled, attempt.Outcome);
        Assert.Equal("CancellationRequested", attempt.ErrorCode);
    }

    [Fact]
    public async Task Actual_job_timeout_records_timed_out_attempt()
    {
        Job job = new(Guid.NewGuid(), "blocking", "{}", JobPriority.Normal, 1, 1, DateTimeOffset.UtcNow);
        job.Queue(DateTimeOffset.UtcNow);
        await AddJobAsync(DatabaseOptions, job);
        await using TaskForgeDbContext context = new(DatabaseOptions);
        EfCoreJobStore store = new(context);
        JobExecutor executor = CreateExecutor(store, [new BlockingJobHandler()], timeProvider: TimeProvider.System);

        await executor.ProcessNextAsync("worker-timeout", _ => { }, CancellationToken.None, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(30));

        JobAttempt attempt = Assert.Single(await ReadAttemptsAsync(job.Id));
        Assert.Equal(JobAttemptOutcome.TimedOut, attempt.Outcome);
        Assert.Equal("Timeout", attempt.ErrorCode);
        Assert.Equal("Job timed out after 1 second(s).", attempt.ErrorMessage);
        Assert.True(attempt.DurationMilliseconds >= 900);
        Assert.Equal(JobStatus.Retrying, (await store.FindAsync(job.Id))!.Status);
    }

    [Fact]
    public async Task Host_shutdown_records_abandoned_attempt_and_preserves_lease_recovery()
    {
        Job job = CreateQueuedJob("blocking", "{}", 1);
        await AddJobAsync(DatabaseOptions, job);
        await using TaskForgeDbContext context = new(DatabaseOptions);
        EfCoreJobStore store = new(context);
        BlockingJobHandler handler = new();
        using CancellationTokenSource stopping = new();
        JobExecutor executor = CreateExecutor(store, [handler]);
        Task<bool> execution = executor.ProcessNextAsync("worker-stopping", _ => { }, CancellationToken.None, stopping.Token);
        await handler.Started.WaitAsync(TimeSpan.FromSeconds(30));

        stopping.Cancel();
        await execution.WaitAsync(TimeSpan.FromSeconds(30));

        JobAttempt attempt = Assert.Single(await ReadAttemptsAsync(job.Id));
        Assert.Equal(JobAttemptOutcome.Abandoned, attempt.Outcome);
        Assert.Equal("HostStopping", attempt.ErrorCode);
        Assert.NotNull(attempt.FinishedAtUtc);
        Assert.Equal(JobStatus.Processing, (await store.FindAsync(job.Id))!.Status);
        await using TaskForgeDbContext recoveryContext = new(DatabaseOptions);
        Assert.Equal(1, await new EfCoreJobStore(recoveryContext).RecoverExpiredLeasesAsync(Now.AddMinutes(1)));
        Assert.Equal("HostStopping", Assert.Single(await ReadAttemptsAsync(job.Id)).ErrorCode);
    }

    [Fact]
    public async Task Queued_cancellation_and_empty_queue_do_not_create_attempts()
    {
        Job job = CreateQueuedJob("successful", "{}", 1);
        job.RequestCancellation(Now);
        await AddJobAsync(DatabaseOptions, job);
        await using TaskForgeDbContext context = new(DatabaseOptions);
        JobExecutor executor = CreateExecutor(new EfCoreJobStore(context), [new SuccessfulJobHandler()]);

        Assert.False(await executor.ProcessNextAsync("worker-01", _ => { }, CancellationToken.None, CancellationToken.None));
        Assert.Empty(await ReadAttemptsAsync(job.Id));
    }

    [Fact]
    public async Task Successful_retry_keeps_the_previous_failure_and_records_a_new_success()
    {
        Job job = CreateQueuedJob("successful", "{}", 1);
        await AddJobAsync(DatabaseOptions, job);
        await using (TaskForgeDbContext context = new(DatabaseOptions))
        {
            SuccessfulJobHandler handler = new(() => throw new InvalidOperationException("Transient failure."));
            JobExecutor executor = CreateExecutor(new EfCoreJobStore(context), [handler]);
            await executor.ProcessNextAsync("worker-01", _ => { }, CancellationToken.None, CancellationToken.None);
        }

        await using (TaskForgeDbContext context = new(DatabaseOptions))
        {
            JobExecutor executor = CreateExecutor(new EfCoreJobStore(context), [new SuccessfulJobHandler()], timeProvider: new FixedTimeProvider(Now.AddSeconds(5)));
            await executor.ProcessNextAsync("worker-02", _ => { }, CancellationToken.None, CancellationToken.None);
        }

        IReadOnlyList<JobAttempt> attempts = await ReadAttemptsAsync(job.Id);
        Assert.Equal([1, 2], attempts.Select(attempt => attempt.AttemptNumber));
        Assert.Equal(JobAttemptOutcome.Failed, attempts[0].Outcome);
        Assert.Equal("Transient failure.", attempts[0].ErrorMessage);
        Assert.Equal(JobAttemptOutcome.Succeeded, attempts[1].Outcome);
        Assert.Equal("worker-02", attempts[1].WorkerId);
        Assert.Null(attempts[1].ErrorCode);
        Assert.Null(attempts[1].ErrorMessage);
    }

    private async Task<IReadOnlyList<JobAttempt>> ReadAttemptsAsync(Guid jobId)
    {
        await using TaskForgeDbContext context = new(DatabaseOptions);
        return (await new EfCoreJobStore(context).GetAttemptsAsync(jobId))!;
    }

    private sealed class CompletionSaveInterceptor : SaveChangesInterceptor
    {
        private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Ready => _ready.Task;
        public bool ConcurrencyConflictObserved { get; private set; }

        public void Release() => _released.TrySetResult();

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (eventData.Context!.ChangeTracker.Entries<Job>().Any(entry => entry.Entity.Status == JobStatus.Completed))
            {
                _ready.TrySetResult();
                await _released.Task.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
            }

            return result;
        }

        public override ValueTask<InterceptionResult> ThrowingConcurrencyExceptionAsync(ConcurrencyExceptionEventData eventData, InterceptionResult result, CancellationToken cancellationToken = default)
        {
            ConcurrencyConflictObserved = true;
            return ValueTask.FromResult(result);
        }
    }

    private static async Task AddJobAsync(DbContextOptions<TaskForgeDbContext> databaseOptions, Job job)
    {
        await using TaskForgeDbContext setupContext = new(databaseOptions);
        await new EfCoreJobStore(setupContext).AddAsync(job);
    }

    private static async Task<Job?> ProcessFailingJobAsync(DbContextOptions<TaskForgeDbContext> databaseOptions, Guid jobId, TimeProvider timeProvider, WorkerOptions options)
    {
        await using TaskForgeDbContext workerContext = new(databaseOptions);
        EfCoreJobStore store = new(workerContext);
        JobExecutor executor = CreateExecutor(
            store,
            [new FailingJobHandler()],
            timeProvider: timeProvider,
            options: options);

        await executor.ProcessNextAsync(
            "worker-01",
            _ => { },
            CancellationToken.None,
            CancellationToken.None);

        return await store.FindAsync(jobId);
    }

    private static JobExecutor CreateExecutor(IJobQueue jobQueue, IEnumerable<IJobHandler> handlers, JobCancellationRegistry? cancellationRegistry = null, TimeProvider? timeProvider = null, WorkerOptions? options = null) =>
        new(
            jobQueue,
            handlers,
            cancellationRegistry ?? new JobCancellationRegistry(),
            timeProvider ?? new FixedTimeProvider(Now),
            Options.Create(options ?? new WorkerOptions
            {
                Count = 1,
                PollIntervalMilliseconds = 50,
                RetryDelaySeconds = 5,
                MaxRetryDelaySeconds = 300,
                LeaseGraceSeconds = 30
            }),
            NullLogger<JobExecutor>.Instance);

    private static Job CreateQueuedJob(string type, string payload, int maxRetries)
    {
        Job job = new(
            Guid.NewGuid(),
            type,
            payload,
            JobPriority.Normal,
            maxRetries,
            timeoutSeconds: 5,
            Now);
        job.Queue(Now);
        return job;
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class AdjustableTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;

        public override DateTimeOffset GetUtcNow() => _now;

        public void SetUtcNow(DateTimeOffset now) => _now = now;
    }

    private sealed class FailingJobHandler : IJobHandler
    {
        public string JobType => "failing";

        public string? ValidatePayload(string payloadJson) => null;

        public Task<string?> HandleAsync(string payloadJson, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Expected failure.");
    }

    private sealed class NonRetryableJobHandler : IJobHandler
    {
        public string JobType => "permanent-failure";

        public string? ValidatePayload(string payloadJson) => null;

        public Task<string?> HandleAsync(string payloadJson, CancellationToken cancellationToken = default) =>
            throw new NonRetryableJobException("Permanent failure.");
    }

    private sealed class TimeoutJobHandler : IJobHandler
    {
        public string JobType => "timeout";

        public string? ValidatePayload(string payloadJson) => null;

        public Task<string?> HandleAsync(string payloadJson, CancellationToken cancellationToken = default) =>
            throw new TaskCanceledException("Expected timeout.");
    }

    private sealed class SuccessfulJobHandler(Action? onExecute = null) : IJobHandler
    {
        public string JobType => "successful";

        public string? ValidatePayload(string payloadJson) => null;

        public Task<string?> HandleAsync(string payloadJson, CancellationToken cancellationToken = default)
        {
            onExecute?.Invoke();
            return Task.FromResult<string?>(null);
        }
    }

    private sealed class BlockingJobHandler : IJobHandler
    {
        private readonly TaskCompletionSource _started =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public string JobType => "blocking";
        public Task Started => _started.Task;
        public bool WasCancelled { get; private set; }

        public string? ValidatePayload(string payloadJson) => null;

        public async Task<string?> HandleAsync(string payloadJson, CancellationToken cancellationToken = default)
        {
            _started.SetResult();

            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return null;
            }
            catch (OperationCanceledException)
                when (cancellationToken.IsCancellationRequested)
            {
                WasCancelled = true;
                throw;
            }
        }
    }
}
