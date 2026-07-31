using TaskForge.Application.Abstractions.Execution;
using TaskForge.Application.Abstractions.Persistence;
using TaskForge.Application.Common.Exceptions;
using TaskForge.Application.Jobs;
using TaskForge.Application.Jobs.Models;
using TaskForge.Application.Jobs.Validation;
using TaskForge.Domain.Jobs;

namespace TaskForge.UnitTests.Application.Jobs;

public sealed class JobManagerTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 7, 26, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Valid_command_creates_queues_and_persists_job()
    {
        FakeJobRepository repository = new();
        JobManager manager = CreateManager(
            repository,
            new StubJobHandler("generate-report"));
        SubmitJobCommand command = new(
            " generate-report ",
            """{"reportName":"Monthly report"}""",
            JobPriority.High,
            MaxRetries: 3,
            TimeoutSeconds: 30);

        Job job = await manager.SubmitAsync(command);

        Assert.Equal("generate-report", job.Type);
        Assert.Equal(JobStatus.Queued, job.Status);
        Assert.Equal(Now, job.CreatedAtUtc);
        Assert.Equal(Now, job.QueuedAtUtc);
        Assert.Same(job, Assert.Single(repository.Jobs));
    }

    [Fact]
    public async Task Invalid_command_is_rejected_before_persistence()
    {
        FakeJobRepository repository = new();
        JobManager manager = CreateManager(repository);
        SubmitJobCommand command = new(
            string.Empty,
            "null",
            JobPriority.Normal,
            MaxRetries: -1,
            TimeoutSeconds: 0);

        ApplicationValidationException exception =
            await Assert.ThrowsAsync<ApplicationValidationException>(
                () => manager.SubmitAsync(command));

        Assert.Equal(
            ["Type", "Payload", "MaxRetries", "TimeoutSeconds"],
            exception.Errors.Keys);
        Assert.Empty(repository.Jobs);
    }

    [Fact]
    public async Task Unsupported_job_type_is_rejected_before_persistence()
    {
        FakeJobRepository repository = new();
        JobManager manager = CreateManager(repository);
        SubmitJobCommand command = new(
            "unknown",
            """{"value":1}""",
            JobPriority.Normal,
            MaxRetries: 3,
            TimeoutSeconds: 30);

        ApplicationValidationException exception =
            await Assert.ThrowsAsync<ApplicationValidationException>(
                () => manager.SubmitAsync(command));

        Assert.Equal(
            ["Job type 'unknown' is not supported."],
            exception.Errors["Type"]);
        Assert.Empty(repository.Jobs);
    }

    [Fact]
    public async Task Handler_payload_error_is_rejected_before_persistence()
    {
        FakeJobRepository repository = new();
        JobManager manager = CreateManager(
            repository,
            new StubJobHandler(
                "example",
                "The example payload is invalid."));
        SubmitJobCommand command = new(
            "example",
            """{"value":1}""",
            JobPriority.Normal,
            MaxRetries: 3,
            TimeoutSeconds: 30);

        ApplicationValidationException exception =
            await Assert.ThrowsAsync<ApplicationValidationException>(
                () => manager.SubmitAsync(command));

        Assert.Equal(
            ["The example payload is invalid."],
            exception.Errors["Payload"]);
        Assert.Empty(repository.Jobs);
    }

    [Theory]
    [InlineData("")]
    [InlineData("null")]
    [InlineData("{")]
    public async Task Missing_or_invalid_payload_is_rejected(string payloadJson)
    {
        FakeJobRepository repository = new();
        JobManager manager = CreateManager(repository);
        SubmitJobCommand command = new(
            "generate-report",
            payloadJson,
            JobPriority.Normal,
            MaxRetries: 3,
            TimeoutSeconds: 30);

        ApplicationValidationException exception =
            await Assert.ThrowsAsync<ApplicationValidationException>(
                () => manager.SubmitAsync(command));

        Assert.Contains("Payload", exception.Errors.Keys);
        Assert.Empty(repository.Jobs);
    }

    [Fact]
    public async Task Query_methods_read_through_repository()
    {
        FakeJobRepository repository = new();
        JobManager manager = CreateManager(
            repository,
            new StubJobHandler("generate-report"));
        Job job = await manager.SubmitAsync(new SubmitJobCommand(
            "generate-report",
            """{"reportName":"Monthly report"}""",
            JobPriority.Normal,
            MaxRetries: 3,
            TimeoutSeconds: 30));

        IReadOnlyList<Job> jobs = await manager.GetAllAsync();
        Job? foundJob = await manager.GetByIdAsync(job.Id);

        Assert.Same(job, Assert.Single(jobs));
        Assert.Same(job, foundJob);
    }

    [Fact]
    public async Task Repeated_idempotency_key_returns_original_job()
    {
        FakeJobRepository repository = new();
        JobManager manager = CreateManager(
            repository,
            new StubJobHandler("example"));
        SubmitJobCommand command = new(
            "example",
            """{"value":1}""",
            JobPriority.Normal,
            MaxRetries: 1,
            TimeoutSeconds: 5);

        JobSubmissionResult first = await manager.SubmitAsync(
            command,
            "request-123");
        JobSubmissionResult replay = await manager.SubmitAsync(
            command,
            "request-123");

        Assert.True(first.Created);
        Assert.False(replay.Created);
        Assert.Same(first.Job, replay.Job);
        Assert.Single(repository.Jobs);
    }

    [Fact]
    public async Task Reused_key_with_different_job_is_rejected()
    {
        FakeJobRepository repository = new();
        JobManager manager = CreateManager(
            repository,
            new StubJobHandler("example"));
        SubmitJobCommand firstCommand = new(
            "example",
            """{"value":1}""",
            JobPriority.Normal,
            MaxRetries: 1,
            TimeoutSeconds: 5);
        SubmitJobCommand differentCommand = firstCommand with
        {
            PayloadJson = """{"value":2}"""
        };
        await manager.SubmitAsync(firstCommand, "request-123");

        await Assert.ThrowsAsync<IdempotencyConflictException>(
            () => manager.SubmitAsync(
                differentCommand,
                "request-123"));
    }

    private static JobManager CreateManager(
        FakeJobRepository repository,
        params IJobHandler[] handlers) =>
        new(
            repository,
            new SubmitJobValidator(handlers),
            new FixedTimeProvider(Now));

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class FakeJobRepository : IJobRepository
    {
        public List<Job> Jobs { get; } = [];

        public Task AddAsync(
            Job job,
            CancellationToken cancellationToken = default)
        {
            Jobs.Add(job);
            return Task.CompletedTask;
        }

        public Task<Job> AddOrGetExistingAsync(
            Job job,
            CancellationToken cancellationToken = default)
        {
            Job? existing = job.IdempotencyKey is null
                ? null
                : Jobs.SingleOrDefault(
                    candidate =>
                        candidate.IdempotencyKey == job.IdempotencyKey);
            if (existing is not null)
            {
                return Task.FromResult(existing);
            }

            Jobs.Add(job);
            return Task.FromResult(job);
        }

        public Task<IReadOnlyList<Job>> GetAllAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<Job>>(Jobs);

        public Task<Job?> FindAsync(
            Guid id,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(Jobs.SingleOrDefault(job => job.Id == id));

        public Task<Job?> FindByIdempotencyKeyAsync(
            string idempotencyKey,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(
                Jobs.SingleOrDefault(
                    job => job.IdempotencyKey == idempotencyKey));

        public Task<Job?> TryAcquireAsync(
            Guid id,
            string workerId,
            DateTimeOffset leaseExpiresAtUtc,
            DateTimeOffset now,
            CancellationToken cancellationToken = default)
        {
            Job? job = Jobs.SingleOrDefault(candidate => candidate.Id == id);
            if (job?.Status != JobStatus.Queued)
            {
                return Task.FromResult<Job?>(null);
            }

            job.StartProcessing(workerId, leaseExpiresAtUtc, now);
            return Task.FromResult<Job?>(job);
        }

        public Task<bool> TryUpdateAsync(
            Job job,
            long expectedVersion,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(true);
    }

    private sealed class StubJobHandler(
        string jobType,
        string? validationError = null)
        : IJobHandler
    {
        public string JobType { get; } = jobType;

        public string? ValidatePayload(string payloadJson) => validationError;

        public Task<string?> HandleAsync(
            string payloadJson,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<string?>(null);
    }
}