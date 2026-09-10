using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

using TaskForge.Infrastructure.Persistence;

namespace TaskForge.UnitTests.Persistence;

public sealed class SqliteWorkerSettingsStoreTests
{
    [Fact]
    public async Task Worker_count_is_persisted_across_contexts()
    {
        await using SqliteConnection connection = new("Data Source=:memory:");
        await connection.OpenAsync();

        DbContextOptions<TaskForgeDbContext> options =
            new DbContextOptionsBuilder<TaskForgeDbContext>()
                .UseSqlite(connection)
                .Options;

        await using (TaskForgeDbContext writeContext = new(options))
        {
            await writeContext.Database.EnsureCreatedAsync();
            EfCoreWorkerSettingsStore store = new(writeContext);
            await store.SetDesiredWorkerCountAsync(
                4,
                new DateTimeOffset(
                    2026,
                    7,
                    28,
                    12,
                    0,
                    0,
                    TimeSpan.Zero));
        }

        await using TaskForgeDbContext readContext = new(options);
        EfCoreWorkerSettingsStore readStore = new(readContext);

        int? workerCount = await readStore.GetDesiredWorkerCountAsync();

        Assert.Equal(4, workerCount);
    }
}