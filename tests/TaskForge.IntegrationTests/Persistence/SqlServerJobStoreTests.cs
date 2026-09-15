using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

using TaskForge.Application.Jobs.Models;
using TaskForge.Domain.Jobs;
using TaskForge.Infrastructure.Persistence;

namespace TaskForge.IntegrationTests.Persistence;

[Collection("SQL Server")]
public sealed class SqlServerJobStoreTests(SqlServerFixture fixture) : SqlServerTest(fixture)
{
    [Fact]
    public async Task Added_job_is_available_from_a_new_context()
    {
        DbContextOptions<TaskForgeDbContext> options = DatabaseOptions;

        Job job = new(
            Guid.Parse("7c077bba-bab3-4e20-a47f-a0ec51838a18"),
            "generate-report",
            """{"reportName":"Monthly report"}""",
            JobPriority.High,
            maxRetries: 3,
            timeoutSeconds: 30,
            new DateTimeOffset(2026, 7, 20, 12, 0, 0, TimeSpan.Zero));
        job.Queue(new DateTimeOffset(2026, 7, 20, 12, 0, 1, TimeSpan.Zero));

        await using (TaskForgeDbContext writeContext = new(options))
        {
            EfCoreJobStore writeStore = new(writeContext);
            await writeStore.AddAsync(job);
        }

        await using TaskForgeDbContext readContext = new(options);
        EfCoreJobStore readStore = new(readContext);

        Job? persistedJob = await readStore.FindAsync(job.Id);

        Assert.NotNull(persistedJob);
        Assert.Equal(job.Type, persistedJob.Type);
        Assert.Equal(job.PayloadJson, persistedJob.PayloadJson);
        Assert.Equal(JobPriority.High, persistedJob.Priority);
        Assert.Equal(JobStatus.Queued, persistedJob.Status);
        Assert.Equal(job.QueuedAtUtc, persistedJob.QueuedAtUtc);
        Assert.Equal(1, persistedJob.Version);
    }

    [Fact]
    public async Task Jobs_are_returned_newest_first()
    {
        DbContextOptions<TaskForgeDbContext> options = DatabaseOptions;

        await using TaskForgeDbContext dbContext = new(options);

        DateTimeOffset now = new(2026, 7, 20, 12, 0, 0, TimeSpan.Zero);
        Job older = CreateJob(Guid.NewGuid(), now);
        Job newer = CreateJob(Guid.NewGuid(), now.AddMinutes(1));
        EfCoreJobStore store = new(dbContext);

        await store.AddAsync(older);
        await store.AddAsync(newer);

        JobPage page = await store.GetPageAsync(new ListJobsQuery());

        Assert.Equal([newer.Id, older.Id], page.Items.Select(job => job.Id));
    }

    [Fact]
    public async Task Jobs_are_filtered_and_paginated_in_the_database()
    {
        DbContextOptions<TaskForgeDbContext> options = DatabaseOptions;

        await using TaskForgeDbContext dbContext = new(options);
        EfCoreJobStore store = new(dbContext);
        DateTimeOffset now =
            new(2026, 7, 20, 12, 0, 0, TimeSpan.Zero);

        await store.AddAsync(CreateJob(Guid.NewGuid(), now, "http-request"));
        await store.AddAsync(CreateJob(
            Guid.NewGuid(),
            now.AddMinutes(1),
            "generate-report"));
        Job olderMatch = CreateJob(
            Guid.NewGuid(),
            now.AddMinutes(2),
            "http-request");
        Job newerMatch = CreateJob(
            Guid.NewGuid(),
            now.AddMinutes(3),
            "http-request");
        await store.AddAsync(olderMatch);
        await store.AddAsync(newerMatch);

        JobPage result = await store.GetPageAsync(new ListJobsQuery(
            Status: JobStatus.Queued,
            Type: "HTTP-REQUEST",
            Page: 2,
            PageSize: 1));

        Assert.Equal(3, result.TotalCount);
        Assert.Equal(3, result.TotalPages);
        Assert.Equal(olderMatch.Id, Assert.Single(result.Items).Id);
    }

    [Fact]
    public async Task Queued_job_can_only_be_acquired_once_by_competing_workers()
    {
        DbContextOptions<TaskForgeDbContext> options = DatabaseOptions;

        Job job = CreateJob(
            Guid.NewGuid(),
            new DateTimeOffset(2026, 7, 20, 12, 0, 0, TimeSpan.Zero));

        await using (TaskForgeDbContext setupContext = new(options))
        {
            await new EfCoreJobStore(setupContext).AddAsync(job);
        }

        DateTimeOffset now = new(2026, 7, 20, 12, 1, 0, TimeSpan.Zero);
        options = new DbContextOptionsBuilder<TaskForgeDbContext>(options)
            .AddInterceptors(new ConcurrentSaveInterceptor())
            .Options;
        await using TaskForgeDbContext firstContext = new(options);
        await using TaskForgeDbContext secondContext = new(options);
        EfCoreJobStore firstStore = new(firstContext);
        EfCoreJobStore secondStore = new(secondContext);

        Task<Job?> first = firstStore.TryAcquireNextAsync("worker-01", TimeSpan.FromSeconds(30), now);
        Task<Job?> second = secondStore.TryAcquireNextAsync("worker-02", TimeSpan.FromSeconds(30), now);
        Job?[] results = await Task.WhenAll(first, second);

        Job acquired = Assert.Single(results.OfType<Job>());
        Assert.Single(results, result => result is null);
        Assert.Equal(JobStatus.Processing, acquired.Status);
        await using TaskForgeDbContext readContext = new(DatabaseOptions);
        Job persisted = await readContext.Jobs.AsNoTracking().SingleAsync();
        Assert.Equal(acquired.OwningWorkerId, persisted.OwningWorkerId);
        Assert.Equal(job.Version + 1, persisted.Version);
    }

    [Fact]
    public async Task Stale_job_update_is_rejected()
    {
        DbContextOptions<TaskForgeDbContext> options = DatabaseOptions;

        Job job = CreateJob(
            Guid.NewGuid(),
            new DateTimeOffset(2026, 7, 20, 12, 0, 0, TimeSpan.Zero));

        await using (TaskForgeDbContext setupContext = new(options))
        {
            await new EfCoreJobStore(setupContext).AddAsync(job);
        }

        await using TaskForgeDbContext firstContext = new(options);
        await using TaskForgeDbContext secondContext = new(options);
        EfCoreJobStore firstStore = new(firstContext);
        EfCoreJobStore secondStore = new(secondContext);
        Job firstCopy = (await firstStore.FindAsync(job.Id))!;
        Job staleCopy = (await secondStore.FindAsync(job.Id))!;
        long expectedVersion = firstCopy.Version;
        DateTimeOffset now = new(2026, 7, 20, 12, 1, 0, TimeSpan.Zero);
        firstCopy.StartProcessing("worker-01", now.AddMinutes(1), now);
        staleCopy.StartProcessing("worker-02", now.AddMinutes(1), now);

        bool firstUpdated = await firstStore.TryUpdateAsync(
            firstCopy,
            expectedVersion);
        bool staleUpdated = await secondStore.TryUpdateAsync(
            staleCopy,
            expectedVersion);

        Assert.True(firstUpdated);
        Assert.False(staleUpdated);
        await using TaskForgeDbContext readContext = new(DatabaseOptions);
        Job persisted = await readContext.Jobs.AsNoTracking().SingleAsync();
        Assert.Equal("worker-01", persisted.OwningWorkerId);
        Assert.Equal(expectedVersion + 1, persisted.Version);
    }

    [Fact]
    public async Task Next_job_is_acquired_by_priority_then_age()
    {
        DbContextOptions<TaskForgeDbContext> options = DatabaseOptions;

        DateTimeOffset now =
            new(2026, 7, 20, 12, 10, 0, TimeSpan.Zero);
        Job lowPriority = CreateJob(Guid.NewGuid(), now.AddMinutes(-2));
        Job highPriority = new(
            Guid.NewGuid(),
            "example",
            """{"value":1}""",
            JobPriority.High,
            maxRetries: 0,
            timeoutSeconds: 5,
            now.AddMinutes(-1));
        highPriority.Queue(now.AddMinutes(-1));

        await using (TaskForgeDbContext setupContext = new(options))
        {
            EfCoreJobStore setupStore = new(setupContext);
            await setupStore.AddAsync(lowPriority);
            await setupStore.AddAsync(highPriority);
        }

        await using TaskForgeDbContext workerContext = new(options);
        EfCoreJobStore store = new(workerContext);
        Job? acquired = await store.TryAcquireNextAsync(
            "worker-01",
            TimeSpan.FromSeconds(30),
            now);

        Assert.NotNull(acquired);
        Assert.Equal(highPriority.Id, acquired.Id);
        Assert.Equal(JobStatus.Processing, acquired.Status);
    }

    [Fact]
    public async Task Idempotency_key_returns_the_original_job()
    {
        DbContextOptions<TaskForgeDbContext> options = DatabaseOptions;
        DateTimeOffset now =
            new(2026, 7, 20, 12, 10, 0, TimeSpan.Zero);
        Job original = new(
            Guid.NewGuid(),
            "example",
            """{"value":1}""",
            JobPriority.Normal,
            maxRetries: 1,
            timeoutSeconds: 5,
            now,
            idempotencyKey: "request-123");
        original.Queue(now);

        await using (TaskForgeDbContext firstContext = new(options))
        {
            await new EfCoreJobStore(firstContext).AddOrGetExistingAsync(original);
        }

        Job duplicate = new(
            Guid.NewGuid(),
            original.Type,
            original.PayloadJson,
            original.Priority,
            original.MaxRetries,
            original.TimeoutSeconds,
            now,
            idempotencyKey: "request-123");
        duplicate.Queue(now);
        await using TaskForgeDbContext secondContext = new(options);

        Job persisted = await new EfCoreJobStore(secondContext)
            .AddOrGetExistingAsync(duplicate);

        Assert.Equal(original.Id, persisted.Id);
        Assert.NotEqual(duplicate.Id, persisted.Id);
    }

    [Fact]
    public async Task Duplicate_idempotency_key_is_rejected_by_the_database()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        await using (TaskForgeDbContext firstContext = new(DatabaseOptions))
        {
            await new EfCoreJobStore(firstContext).AddAsync(CreateJob(Guid.NewGuid(), now, idempotencyKey: "request-123"));
        }

        await using TaskForgeDbContext secondContext = new(DatabaseOptions);
        Job duplicate = CreateJob(Guid.NewGuid(), now, idempotencyKey: "request-123");
        DbUpdateException exception = await Assert.ThrowsAsync<DbUpdateException>(
            () => new EfCoreJobStore(secondContext).AddAsync(duplicate));

        SqlException sqlException = Assert.IsType<SqlException>(exception.InnerException);
        Assert.Contains(sqlException.Number, new[] { 2601, 2627 });
        await using TaskForgeDbContext readContext = new(DatabaseOptions);
        Assert.Equal(1, await readContext.Jobs.CountAsync());
    }

    [Fact]
    public async Task Concurrent_idempotent_submissions_return_the_same_job()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        DbContextOptions<TaskForgeDbContext> options = new DbContextOptionsBuilder<TaskForgeDbContext>(DatabaseOptions)
            .AddInterceptors(new ConcurrentSaveInterceptor())
            .Options;
        await using TaskForgeDbContext firstContext = new(options);
        await using TaskForgeDbContext secondContext = new(options);
        Job firstJob = CreateJob(Guid.NewGuid(), now, idempotencyKey: "request-123");
        Job secondJob = CreateJob(Guid.NewGuid(), now, idempotencyKey: "request-123");

        Job[] results = await Task.WhenAll(
            new EfCoreJobStore(firstContext).AddOrGetExistingAsync(firstJob),
            new EfCoreJobStore(secondContext).AddOrGetExistingAsync(secondJob));

        Assert.Equal(results[0].Id, results[1].Id);
        await using TaskForgeDbContext readContext = new(DatabaseOptions);
        Assert.Equal(results[0].Id, (await readContext.Jobs.SingleAsync()).Id);
    }

    [Fact]
    public async Task Due_retries_are_ordered_by_priority_then_due_time_and_future_retries_are_excluded()
    {
        DateTimeOffset now = new(2026, 7, 20, 12, 10, 0, TimeSpan.Zero);
        Job future = CreateRetryJob(now.AddMinutes(-10), now.AddSeconds(1), JobPriority.High);
        Job earlierRetry = CreateRetryJob(now.AddMinutes(-8), now.AddMinutes(-2), JobPriority.Normal);
        Job laterRetry = CreateRetryJob(now.AddMinutes(-9), now.AddMinutes(-1), JobPriority.Normal);
        Job highPriority = CreateRetryJob(now.AddMinutes(-7), now, JobPriority.High);
        Job queued = CreateJob(Guid.NewGuid(), now.AddSeconds(-31));
        await using (TaskForgeDbContext setupContext = new(DatabaseOptions))
        {
            setupContext.Jobs.AddRange(future, laterRetry, queued, earlierRetry, highPriority);
            await setupContext.SaveChangesAsync();
        }

        foreach (Guid expectedId in new[] { highPriority.Id, earlierRetry.Id, laterRetry.Id, queued.Id })
        {
            await using TaskForgeDbContext workerContext = new(DatabaseOptions);
            Job? acquired = await new EfCoreJobStore(workerContext).TryAcquireNextAsync("worker-01", TimeSpan.FromSeconds(30), now);
            Assert.NotNull(acquired);
            Assert.Equal(expectedId, acquired.Id);
            Assert.Equal(JobStatus.Processing, acquired.Status);
            Assert.Null(acquired.NextRetryAtUtc);
        }

        await using TaskForgeDbContext readContext = new(DatabaseOptions);
        EfCoreJobStore store = new(readContext);
        Assert.Null(await store.TryAcquireNextAsync("worker-02", TimeSpan.FromSeconds(30), now));
        Job? remaining = await store.FindAsync(future.Id);
        Assert.NotNull(remaining);
        Assert.Equal(JobStatus.Retrying, remaining.Status);
        Assert.Equal(now.AddSeconds(1), remaining.NextRetryAtUtc);
    }

    [Fact]
    public async Task Expired_leases_are_recovered_once_and_active_leases_are_preserved()
    {
        DateTimeOffset now = new(2026, 7, 20, 12, 10, 0, TimeSpan.Zero);
        Job expired = CreateJob(Guid.NewGuid(), now.AddMinutes(-2));
        Job cancelled = CreateJob(Guid.NewGuid(), now.AddMinutes(-2));
        Job active = CreateJob(Guid.NewGuid(), now.AddMinutes(-2));
        expired.StartProcessing("lost-worker", now, now.AddMinutes(-1));
        cancelled.StartProcessing("lost-worker", now, now.AddMinutes(-1));
        cancelled.RequestCancellation(now.AddSeconds(-1));
        active.StartProcessing("active-worker", now.AddMinutes(1), now.AddMinutes(-1));
        await using (TaskForgeDbContext setupContext = new(DatabaseOptions))
        {
            setupContext.Jobs.AddRange(expired, cancelled, active);
            await setupContext.SaveChangesAsync();
        }

        await using (TaskForgeDbContext recoveryContext = new(DatabaseOptions))
        {
            EfCoreJobStore store = new(recoveryContext);
            Assert.Equal(2, await store.RecoverExpiredLeasesAsync(now));
            Assert.Equal(0, await store.RecoverExpiredLeasesAsync(now));
        }

        await using TaskForgeDbContext readContext = new(DatabaseOptions);
        EfCoreJobStore readStore = new(readContext);
        Job recovered = (await readStore.FindAsync(expired.Id))!;
        Assert.Equal(JobStatus.Queued, recovered.Status);
        Assert.Null(recovered.OwningWorkerId);
        Assert.Null(recovered.LeaseExpiresAtUtc);
        Assert.Equal(expired.Version + 1, recovered.Version);
        Job persistedCancellation = (await readStore.FindAsync(cancelled.Id))!;
        Assert.Equal(JobStatus.Cancelled, persistedCancellation.Status);
        Assert.Null(persistedCancellation.OwningWorkerId);
        Assert.Null(persistedCancellation.LeaseExpiresAtUtc);
        Job persistedActive = (await readStore.FindAsync(active.Id))!;
        Assert.Equal(JobStatus.Processing, persistedActive.Status);
        Assert.Equal(active.Version, persistedActive.Version);
        Assert.Equal("active-worker", persistedActive.OwningWorkerId);
        Assert.Equal(active.LeaseExpiresAtUtc, persistedActive.LeaseExpiresAtUtc);
        Job? reacquired = await readStore.TryAcquireNextAsync("replacement-worker", TimeSpan.FromSeconds(30), now);
        Assert.NotNull(reacquired);
        Assert.Equal(expired.Id, reacquired.Id);
        Assert.Equal("replacement-worker", reacquired.OwningWorkerId);
    }

    private static Job CreateRetryJob(DateTimeOffset createdAt, DateTimeOffset dueAt, JobPriority priority)
    {
        Job job = new(Guid.NewGuid(), "example", "{}", priority, 3, 30, createdAt);
        job.Queue(createdAt);
        job.StartProcessing("previous-worker", createdAt.AddMinutes(1), createdAt);
        job.Fail("Expected failure.", dueAt, createdAt.AddSeconds(1));
        return job;
    }

    private static Job CreateJob(Guid id, DateTimeOffset createdAt, string type = "generate-report", string? idempotencyKey = null)
    {
        Job job = new(
            id,
            type,
            """{"reportName":"Monthly report"}""",
            JobPriority.Normal,
            maxRetries: 3,
            timeoutSeconds: 30,
            createdAt,
            idempotencyKey);
        job.Queue(createdAt.AddSeconds(1));
        return job;
    }
}
