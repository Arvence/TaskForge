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
        JobManager manager = new(
            repository,
            new SubmitJobValidator(),
            new FixedTimeProvider(Now));
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
        JobManager manager = new(
            repository,
            new SubmitJobValidator(),
            new FixedTimeProvider(Now));
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

    [Theory]
    [InlineData("")]
    [InlineData("null")]
    [InlineData("{")]
    public async Task Missing_or_invalid_payload_is_rejected(string payloadJson)
    {
        FakeJobRepository repository = new();
        JobManager manager = new(
            repository,
            new SubmitJobValidator(),
            new FixedTimeProvider(Now));
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
        JobManager manager = new(
            repository,
            new SubmitJobValidator(),
            new FixedTimeProvider(Now));
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

        public Task<IReadOnlyList<Job>> GetAllAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<Job>>(Jobs);

        public Task<Job?> FindAsync(
            Guid id,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(Jobs.SingleOrDefault(job => job.Id == id));
    }
}
