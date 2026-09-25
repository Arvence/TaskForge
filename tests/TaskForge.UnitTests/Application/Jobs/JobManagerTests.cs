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
        JobManager manager = CreateManager(repository);
        SubmitJobCommand command = new(
            " A-Project ",
            " generate-report ",
            """{"reportName":"Monthly report"}""",
            JobPriority.High,
            MaxRetries: 3,
            TimeoutSeconds: 30);

        Job job = await manager.SubmitAsync(command);

        Assert.Equal("a-project", job.ApplicationId);
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
            "test-app",
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
    [InlineData("send-email", "{}")]
    [InlineData("unknown", "{\"value\":1}")]
    [InlineData("generate-report", "{\"entries\":[]}")]
    [InlineData("http-request", "42")]
    public async Task Arbitrary_types_and_valid_json_are_persisted_without_handlers(string type, string payloadJson)
    {
        FakeJobRepository repository = new();
        JobManager manager = CreateManager(repository);
        SubmitJobCommand command = new("test-app", type, payloadJson, JobPriority.Normal, 3, 30);

        Job job = await manager.SubmitAsync(command);

        Assert.Equal(type, job.Type);
        Assert.Equal(payloadJson, job.PayloadJson);
        Assert.Equal(JobStatus.Queued, job.Status);
        Assert.Same(job, Assert.Single(repository.Jobs));
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
            "test-app",
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
        JobManager manager = CreateManager(repository);
        Job job = await manager.SubmitAsync(new SubmitJobCommand(
            "test-app",
            "generate-report",
            """{"reportName":"Monthly report"}""",
            JobPriority.Normal,
            MaxRetries: 3,
            TimeoutSeconds: 30));

        JobPage page = await manager.GetPageAsync(new ListJobsQuery());
        Job? foundJob = await manager.GetByIdAsync(job.Id);

        Assert.Same(job, Assert.Single(page.Items));
        Assert.Same(job, foundJob);
    }

    [Theory]
    [InlineData(0, 50, "Page")]
    [InlineData(1, 0, "PageSize")]
    [InlineData(1, 101, "PageSize")]
    public async Task Invalid_pagination_is_rejected(
        int page,
        int pageSize,
        string errorKey)
    {
        FakeJobRepository repository = new();
        JobManager manager = CreateManager(repository);

        ApplicationValidationException exception =
            await Assert.ThrowsAsync<ApplicationValidationException>(() =>
                manager.GetPageAsync(new ListJobsQuery(
                    Page: page,
                    PageSize: pageSize)));

        Assert.Contains(errorKey, exception.Errors.Keys);
    }

    [Fact]
    public async Task Repeated_idempotency_key_returns_original_job()
    {
        FakeJobRepository repository = new();
        JobManager manager = CreateManager(repository);
        SubmitJobCommand command = new(
            "test-app",
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
        JobManager manager = CreateManager(repository);
        SubmitJobCommand firstCommand = new(
            "test-app",
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

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("a/b")]
    [InlineData("a b")]
    [InlineData("\u00e4-project")]
    public async Task Invalid_application_id_is_rejected_before_persistence(string? applicationId)
    {
        FakeJobRepository repository = new();
        JobManager manager = CreateManager(repository);
        SubmitJobCommand command = new(applicationId!, "example", "{}", JobPriority.Normal, 3, 30);

        ApplicationValidationException exception = await Assert.ThrowsAsync<ApplicationValidationException>(() => manager.SubmitAsync(command));

        Assert.Equal(["ApplicationId"], exception.Errors.Keys);
        Assert.Empty(repository.Jobs);
    }

    [Fact]
    public async Task Oversized_application_id_is_rejected_for_submission_and_listing()
    {
        FakeJobRepository repository = new();
        JobManager manager = CreateManager(repository);
        string applicationId = new('a', 101);
        SubmitJobCommand command = new(applicationId, "example", "{}", JobPriority.Normal, 3, 30);

        ApplicationValidationException submission = await Assert.ThrowsAsync<ApplicationValidationException>(() => manager.SubmitAsync(command));
        ApplicationValidationException listing = await Assert.ThrowsAsync<ApplicationValidationException>(() => manager.GetPageAsync(new ListJobsQuery(ApplicationId: applicationId)));

        Assert.Contains("ApplicationId", submission.Errors.Keys);
        Assert.Contains("ApplicationId", listing.Errors.Keys);
        Assert.Empty(repository.Jobs);
    }

    [Fact]
    public async Task Idempotency_is_scoped_to_normalized_application_id()
    {
        FakeJobRepository repository = new();
        JobManager manager = CreateManager(repository);
        SubmitJobCommand command = new(" A-Project ", "example", "{}", JobPriority.Normal, 3, 30);

        JobSubmissionResult first = await manager.SubmitAsync(command, "same-key");
        JobSubmissionResult duplicate = await manager.SubmitAsync(command with { ApplicationId = "a-project" }, "same-key");
        JobSubmissionResult other = await manager.SubmitAsync(command with { ApplicationId = "b-project", PayloadJson = "{\"other\":true}" }, "same-key");

        Assert.True(first.Created);
        Assert.False(duplicate.Created);
        Assert.Equal(first.Job.Id, duplicate.Job.Id);
        Assert.True(other.Created);
        Assert.NotEqual(first.Job.Id, other.Job.Id);
        Assert.Equal(2, repository.Jobs.Count);
    }

    [Fact]
    public async Task Application_filter_is_normalized_before_pagination()
    {
        FakeJobRepository repository = new();
        JobManager manager = CreateManager(repository);
        SubmitJobCommand command = new("a-project", "example", "{}", JobPriority.High, 3, 30);
        await manager.SubmitAsync(command);
        await manager.SubmitAsync(command with { ApplicationId = "b-project" });
        Job second = await manager.SubmitAsync(command);

        JobPage page = await manager.GetPageAsync(new ListJobsQuery(Priority: JobPriority.High, Page: 2, PageSize: 1, ApplicationId: " A-PROJECT "));
        JobPage all = await manager.GetPageAsync(new ListJobsQuery());

        Assert.Equal(2, page.TotalCount);
        Assert.Equal(second.Id, Assert.Single(page.Items).Id);
        Assert.Equal(3, all.TotalCount);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("a/b")]
    public async Task Invalid_application_filter_is_rejected(string applicationId)
    {
        JobManager manager = CreateManager(new FakeJobRepository());

        ApplicationValidationException exception = await Assert.ThrowsAsync<ApplicationValidationException>(() => manager.GetPageAsync(new ListJobsQuery(ApplicationId: applicationId)));

        Assert.Contains("ApplicationId", exception.Errors.Keys);
    }

    private static JobManager CreateManager(FakeJobRepository repository) =>
        new(
            repository,
            new SubmitJobValidator(),
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
                        candidate.ApplicationId == job.ApplicationId && candidate.IdempotencyKey == job.IdempotencyKey);
            if (existing is not null)
            {
                return Task.FromResult(existing);
            }

            Jobs.Add(job);
            return Task.FromResult(job);
        }

        public Task<JobPage> GetPageAsync(
            ListJobsQuery query,
            CancellationToken cancellationToken = default)
        {
            IEnumerable<Job> jobs = Jobs;
            if (query.ApplicationId is not null)
            {
                jobs = jobs.Where(job => job.ApplicationId == query.ApplicationId);
            }
            if (query.Status is not null)
            {
                jobs = jobs.Where(job => job.Status == query.Status);
            }

            if (query.Type is not null)
            {
                jobs = jobs.Where(job => string.Equals(
                    job.Type,
                    query.Type,
                    StringComparison.OrdinalIgnoreCase));
            }

            if (query.Priority is not null)
            {
                jobs = jobs.Where(job => job.Priority == query.Priority);
            }

            Job[] filteredJobs = jobs.ToArray();
            Job[] items = filteredJobs
                .Skip((query.Page - 1) * query.PageSize)
                .Take(query.PageSize)
                .ToArray();
            return Task.FromResult(new JobPage(
                items,
                query.Page,
                query.PageSize,
                filteredJobs.Length));
        }

        public Task<Job?> FindAsync(
            Guid id,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(Jobs.SingleOrDefault(job => job.Id == id));

        public Task<Job?> FindByIdempotencyKeyAsync(string applicationId, string idempotencyKey, CancellationToken cancellationToken = default) =>
            Task.FromResult(
                Jobs.SingleOrDefault(
                    job => job.ApplicationId == applicationId && job.IdempotencyKey == idempotencyKey));

        public Task<bool> TryUpdateAsync(
            Job job,
            long expectedVersion,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(true);
    }
}
