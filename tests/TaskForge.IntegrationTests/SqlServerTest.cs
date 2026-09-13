using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

using TaskForge.Infrastructure.Persistence;

namespace TaskForge.IntegrationTests;

public abstract class SqlServerTest : IAsyncLifetime
{
    protected SqlServerTest(SqlServerFixture fixture)
    {
        ConnectionString = new SqlConnectionStringBuilder(fixture.ConnectionString)
        {
            InitialCatalog = $"TaskForgeTest_{Guid.NewGuid():N}",
            Pooling = false
        }.ConnectionString;
        DatabaseOptions = new DbContextOptionsBuilder<TaskForgeDbContext>()
            .UseSqlServer(ConnectionString)
            .Options;
    }

    protected string ConnectionString { get; }
    protected DbContextOptions<TaskForgeDbContext> DatabaseOptions { get; }

    public async Task InitializeAsync()
    {
        await using TaskForgeDbContext context = new(DatabaseOptions);
        await context.Database.MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        await using TaskForgeDbContext context = new(DatabaseOptions);
        await context.Database.EnsureDeletedAsync();
    }
}
