using Microsoft.EntityFrameworkCore;

using TaskForge.Infrastructure.Persistence;

namespace TaskForge.IntegrationTests.Persistence;

[Collection("SQL Server")]
public sealed class SqlServerWorkerSettingsStoreTests(SqlServerFixture fixture) : SqlServerTest(fixture)
{
    [Fact]
    public async Task Worker_count_is_persisted_across_contexts()
    {
        DbContextOptions<TaskForgeDbContext> options = DatabaseOptions;

        await using (TaskForgeDbContext writeContext = new(options))
        {
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