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
