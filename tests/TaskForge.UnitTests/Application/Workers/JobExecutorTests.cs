using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using TaskForge.Application.Abstractions.Execution;
using TaskForge.Application.Jobs;
using TaskForge.Application.Jobs.Models;
using TaskForge.Application.Workers;
using TaskForge.Domain.Jobs;
using TaskForge.Infrastructure.Persistence;

namespace TaskForge.UnitTests.Application.Workers;

public sealed class JobExecutorTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 7, 28, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Successful_job_is_processed_to_completion()
    {
        await using SqliteConnection connection = new("Data Source=:memory:");
        await connection.OpenAsync();
        DbContextOptions<TaskForgeDbContext> databaseOptions =
            CreateDatabaseOptions(connection);
        Job job = CreateQueuedJob(
            "successful",
            """{"value":1}""",
            maxRetries: 0);
        await AddJobAsync(databaseOptions, job);
        await using TaskForgeDbContext workerContext = new(databaseOptions);
        EfCoreJobStore store = new(workerContext);
        JobExecutor executor = CreateExecutor(
            store,
            [new SuccessfulJobHandler()]);
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
    }

    [Fact]
    public async Task Unsupported_job_is_dead_lettered()
    {
        await using SqliteConnection connection = new("Data Source=:memory:");
        await connection.OpenAsync();
        DbContextOptions<TaskForgeDbContext> databaseOptions =
            CreateDatabaseOptions(connection);
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
    }

    [Fact]
    public async Task Failed_job_uses_retry_policy()
    {
        await using SqliteConnection connection = new("Data Source=:memory:");
        await connection.OpenAsync();
        DbContextOptions<TaskForgeDbContext> databaseOptions =
            CreateDatabaseOptions(connection);
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
    }

    [Fact]
    public async Task Non_retryable_failure_is_dead_lettered_immediately()
    {
        await using SqliteConnection connection = new("Data Source=:memory:");
        await connection.OpenAsync();
        DbContextOptions<TaskForgeDbContext> databaseOptions =
            CreateDatabaseOptions(connection);
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
    }

    [Fact]
    public async Task Timeout_failure_is_scheduled_for_retry()
    {
        await using SqliteConnection connection = new("Data Source=:memory:");
        await connection.OpenAsync();
        DbContextOptions<TaskForgeDbContext> databaseOptions =
            CreateDatabaseOptions(connection);
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
        await using SqliteConnection connection = new("Data Source=:memory:");
        await connection.OpenAsync();
        DbContextOptions<TaskForgeDbContext> databaseOptions =
            CreateDatabaseOptions(connection);
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
    }

    [Fact]
    public async Task Running_job_can_be_cancelled()
    {
        string connectionString =
            $"Data Source={Guid.NewGuid():N};Mode=Memory;Cache=Shared";
        await using SqliteConnection anchorConnection = new(connectionString);
        await anchorConnection.OpenAsync();
        DbContextOptions<TaskForgeDbContext> databaseOptions =
            new DbContextOptionsBuilder<TaskForgeDbContext>()
                .UseSqlite(connectionString)
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
        await handler.Started;

        await using TaskForgeDbContext apiContext = new(databaseOptions);
        JobCancellationService cancellationService = new(
            new EfCoreJobStore(apiContext),
            cancellationRegistry,
            new FixedTimeProvider(Now.AddSeconds(1)));
        JobCancellationResult cancellation = await cancellationService.RequestAsync(
            job.Id);

        Assert.True(await execution);
        await using TaskForgeDbContext readContext = new(databaseOptions);
        Job? persistedJob = await new EfCoreJobStore(readContext).FindAsync(job.Id);
        Assert.Equal(JobCancellationStatus.Accepted, cancellation.Status);
        Assert.NotNull(persistedJob);
        Assert.Equal(JobStatus.Cancelled, persistedJob.Status);
        Assert.True(persistedJob.CancellationRequested);
        Assert.True(handler.WasCancelled);
    }

    private static DbContextOptions<TaskForgeDbContext> CreateDatabaseOptions(
        SqliteConnection connection) =>
        new DbContextOptionsBuilder<TaskForgeDbContext>()
            .UseSqlite(connection)
            .Options;

    private static async Task AddJobAsync(
        DbContextOptions<TaskForgeDbContext> databaseOptions,
        Job job)
    {
        await using TaskForgeDbContext setupContext = new(databaseOptions);
        await setupContext.Database.EnsureCreatedAsync();
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

    private static Job CreateQueuedJob(
        string type,
        string payload,
        int maxRetries)
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

        public Task<string?> HandleAsync(
            string payloadJson,
            CancellationToken cancellationToken = default) =>
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

    private sealed class SuccessfulJobHandler : IJobHandler
    {
        public string JobType => "successful";

        public string? ValidatePayload(string payloadJson) => null;

        public Task<string?> HandleAsync(
            string payloadJson,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<string?>(null);
    }

    private sealed class BlockingJobHandler : IJobHandler
    {
        private readonly TaskCompletionSource _started =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public string JobType => "blocking";
        public Task Started => _started.Task;
        public bool WasCancelled { get; private set; }

        public string? ValidatePayload(string payloadJson) => null;

        public async Task<string?> HandleAsync(
            string payloadJson,
            CancellationToken cancellationToken = default)
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
