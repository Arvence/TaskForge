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
