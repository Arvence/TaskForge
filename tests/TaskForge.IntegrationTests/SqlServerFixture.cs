using Testcontainers.MsSql;

namespace TaskForge.IntegrationTests;

public sealed class SqlServerFixture : IAsyncLifetime
{
    private readonly MsSqlContainer _container = new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-latest").Build();

    public string ConnectionString => _container.GetConnectionString();

    public Task InitializeAsync() => _container.StartAsync();

    public Task DisposeAsync() => _container.DisposeAsync().AsTask();
}

[CollectionDefinition("SQL Server")]
public sealed class SqlServerCollection : ICollectionFixture<SqlServerFixture>
{
}
