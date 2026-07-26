using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TaskForge.Application.Abstractions.Persistence;

namespace TaskForge.Infrastructure.Persistence;

public static class SqlitePersistenceExtensions
{
    public static IServiceCollection AddTaskForgeSqlite(
        this IServiceCollection services,
        string connectionString,
        string contentRootPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        ArgumentException.ThrowIfNullOrWhiteSpace(contentRootPath);

        SqliteConnectionStringBuilder connectionStringBuilder = new(connectionString);
        if (string.IsNullOrWhiteSpace(connectionStringBuilder.DataSource))
        {
            throw new InvalidOperationException(
                "The TaskForge SQLite connection string must specify a data source.");
        }

        if (!string.Equals(
                connectionStringBuilder.DataSource,
                ":memory:",
                StringComparison.OrdinalIgnoreCase)
            && !Path.IsPathRooted(connectionStringBuilder.DataSource))
        {
            connectionStringBuilder.DataSource = Path.GetFullPath(
                connectionStringBuilder.DataSource,
                contentRootPath);
        }

        string? databaseDirectory = Path.GetDirectoryName(connectionStringBuilder.DataSource);
        if (!string.IsNullOrWhiteSpace(databaseDirectory))
        {
            Directory.CreateDirectory(databaseDirectory);
        }

        services.AddDbContext<TaskForgeDbContext>(options =>
            options.UseSqlite(connectionStringBuilder.ConnectionString));
        services.AddScoped<IJobRepository, SqliteJobStore>();

        return services;
    }

    public static async Task InitializeTaskForgeDatabaseAsync(
        this IServiceProvider serviceProvider,
        CancellationToken cancellationToken = default)
    {
        await using AsyncServiceScope scope = serviceProvider.CreateAsyncScope();
        TaskForgeDbContext dbContext =
            scope.ServiceProvider.GetRequiredService<TaskForgeDbContext>();
        await dbContext.Database.EnsureCreatedAsync(cancellationToken);
    }
}
