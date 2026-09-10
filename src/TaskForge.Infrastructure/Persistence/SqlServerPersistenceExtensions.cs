using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using TaskForge.Application.Abstractions.Execution;
using TaskForge.Application.Abstractions.Persistence;

namespace TaskForge.Infrastructure.Persistence;

public static class SqlServerPersistenceExtensions
{
    public static IServiceCollection AddTaskForgeSqlServer(this IServiceCollection services, string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        services.AddDbContext<TaskForgeDbContext>(options =>
            options.UseSqlServer(connectionString, sqlServerOptions => sqlServerOptions.EnableRetryOnFailure()));
        services.AddScoped<EfCoreJobStore>();
        services.AddScoped<IJobRepository>(serviceProvider => serviceProvider.GetRequiredService<EfCoreJobStore>());
        services.AddScoped<IJobQueue>(serviceProvider => serviceProvider.GetRequiredService<EfCoreJobStore>());
        services.AddScoped<IWorkerSettingsStore, EfCoreWorkerSettingsStore>();

        return services;
    }

    public static async Task InitializeTaskForgeDatabaseAsync(this IServiceProvider serviceProvider, CancellationToken cancellationToken = default)
    {
        await using AsyncServiceScope scope = serviceProvider.CreateAsyncScope();
        TaskForgeDbContext dbContext =
            scope.ServiceProvider.GetRequiredService<TaskForgeDbContext>();
        await dbContext.Database.EnsureCreatedAsync(cancellationToken);
    }
}
