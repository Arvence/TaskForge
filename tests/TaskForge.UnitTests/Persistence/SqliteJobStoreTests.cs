using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

using TaskForge.Domain.Jobs;
using TaskForge.Infrastructure.Persistence;

namespace TaskForge.UnitTests.Persistence;

public sealed class SqliteJobStoreTests
{
    [Fact]
    public async Task Added_job_is_available_from_a_new_context()
    {
        await using SqliteConnection connection = new("Data Source=:memory:");
        await connection.OpenAsync();

        DbContextOptions<TaskForgeDbContext> options =
            new DbContextOptionsBuilder<TaskForgeDbContext>()
                .UseSqlite(connection)
                .Options;

        await using (TaskForgeDbContext setupContext = new(options))
        {
            await setupContext.Database.EnsureCreatedAsync();
        }

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
            SqliteJobStore writeStore = new(writeContext);
            await writeStore.AddAsync(job);
        }

        await using TaskForgeDbContext readContext = new(options);
        SqliteJobStore readStore = new(readContext);

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
        await using SqliteConnection connection = new("Data Source=:memory:");
        await connection.OpenAsync();

        DbContextOptions<TaskForgeDbContext> options =
            new DbContextOptionsBuilder<TaskForgeDbContext>()
                .UseSqlite(connection)
                .Options;

        await using TaskForgeDbContext dbContext = new(options);
        await dbContext.Database.EnsureCreatedAsync();

        DateTimeOffset now = new(2026, 7, 20, 12, 0, 0, TimeSpan.Zero);
        Job older = CreateJob(Guid.NewGuid(), now);
        Job newer = CreateJob(Guid.NewGuid(), now.AddMinutes(1));
        SqliteJobStore store = new(dbContext);

        await store.AddAsync(older);
        await store.AddAsync(newer);

        IReadOnlyList<Job> jobs = await store.GetAllAsync();

        Assert.Equal([newer.Id, older.Id], jobs.Select(job => job.Id));
    }

    [Fact]
    public async Task Queued_job_can_only_be_acquired_once()
    {
        await using SqliteConnection connection = new("Data Source=:memory:");
        await connection.OpenAsync();

        DbContextOptions<TaskForgeDbContext> options =
            new DbContextOptionsBuilder<TaskForgeDbContext>()
                .UseSqlite(connection)
                .Options;

        Job job = CreateJob(
            Guid.NewGuid(),
            new DateTimeOffset(2026, 7, 20, 12, 0, 0, TimeSpan.Zero));

        await using (TaskForgeDbContext setupContext = new(options))
        {
            await setupContext.Database.EnsureCreatedAsync();
            await new SqliteJobStore(setupContext).AddAsync(job);
        }

        DateTimeOffset now = new(2026, 7, 20, 12, 1, 0, TimeSpan.Zero);
        await using TaskForgeDbContext firstContext = new(options);
        await using TaskForgeDbContext secondContext = new(options);
        SqliteJobStore firstStore = new(firstContext);
        SqliteJobStore secondStore = new(secondContext);

        Job? acquired = await firstStore.TryAcquireAsync(
            job.Id,
            "worker-01",
            now.AddMinutes(1),
            now);
        Job? duplicate = await secondStore.TryAcquireAsync(
            job.Id,
            "worker-02",
            now.AddMinutes(1),
            now);

        Assert.NotNull(acquired);
        Assert.Equal(JobStatus.Processing, acquired.Status);
        Assert.Equal("worker-01", acquired.OwningWorkerId);
        Assert.Null(duplicate);
    }

    [Fact]
    public async Task Stale_job_update_is_rejected()
    {
        await using SqliteConnection connection = new("Data Source=:memory:");
        await connection.OpenAsync();

        DbContextOptions<TaskForgeDbContext> options =
            new DbContextOptionsBuilder<TaskForgeDbContext>()
                .UseSqlite(connection)
                .Options;

        Job job = CreateJob(
            Guid.NewGuid(),
            new DateTimeOffset(2026, 7, 20, 12, 0, 0, TimeSpan.Zero));

        await using (TaskForgeDbContext setupContext = new(options))
        {
            await setupContext.Database.EnsureCreatedAsync();
            await new SqliteJobStore(setupContext).AddAsync(job);
        }

        await using TaskForgeDbContext firstContext = new(options);
        await using TaskForgeDbContext secondContext = new(options);
        SqliteJobStore firstStore = new(firstContext);
        SqliteJobStore secondStore = new(secondContext);
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
    }

    [Fact]
    public async Task Next_job_is_acquired_by_priority_then_age()
    {
        await using SqliteConnection connection = new("Data Source=:memory:");
        await connection.OpenAsync();

        DbContextOptions<TaskForgeDbContext> options =
            new DbContextOptionsBuilder<TaskForgeDbContext>()
                .UseSqlite(connection)
                .Options;

        DateTimeOffset now =
            new(2026, 7, 20, 12, 10, 0, TimeSpan.Zero);
        Job lowPriority = CreateJob(Guid.NewGuid(), now.AddMinutes(-2));
        Job highPriority = new(
            Guid.NewGuid(),
            "delay",
            """{"delayMilliseconds":1}""",
            JobPriority.High,
            maxRetries: 0,
            timeoutSeconds: 5,
            now.AddMinutes(-1));
        highPriority.Queue(now.AddMinutes(-1));

        await using (TaskForgeDbContext setupContext = new(options))
        {
            await setupContext.Database.EnsureCreatedAsync();
            SqliteJobStore setupStore = new(setupContext);
            await setupStore.AddAsync(lowPriority);
            await setupStore.AddAsync(highPriority);
        }

        await using TaskForgeDbContext workerContext = new(options);
        SqliteJobStore store = new(workerContext);
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
        await using SqliteConnection connection = new("Data Source=:memory:");
        await connection.OpenAsync();

        DbContextOptions<TaskForgeDbContext> options =
            new DbContextOptionsBuilder<TaskForgeDbContext>()
                .UseSqlite(connection)
                .Options;
        DateTimeOffset now =
            new(2026, 7, 20, 12, 10, 0, TimeSpan.Zero);
        Job original = new(
            Guid.NewGuid(),
            "delay",
            """{"delayMilliseconds":1}""",
            JobPriority.Normal,
            maxRetries: 1,
            timeoutSeconds: 5,
            now,
            idempotencyKey: "request-123");
        original.Queue(now);

        await using (TaskForgeDbContext firstContext = new(options))
        {
            await firstContext.Database.EnsureCreatedAsync();
            await new SqliteJobStore(firstContext).AddOrGetExistingAsync(original);
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

        Job persisted = await new SqliteJobStore(secondContext)
            .AddOrGetExistingAsync(duplicate);

        Assert.Equal(original.Id, persisted.Id);
        Assert.NotEqual(duplicate.Id, persisted.Id);
    }

    private static Job CreateJob(Guid id, DateTimeOffset createdAt)
    {
        Job job = new(
            id,
            "generate-report",
            """{"reportName":"Monthly report"}""",
            JobPriority.Normal,
            maxRetries: 3,
            timeoutSeconds: 30,
            createdAt);
        job.Queue(createdAt.AddSeconds(1));
        return job;
    }
}