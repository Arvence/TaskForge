using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

using TaskForge.Application.Abstractions.Execution;
using TaskForge.Application.Abstractions.Persistence;
using TaskForge.Application.Workers;
using TaskForge.Infrastructure.Persistence;

namespace TaskForge.UnitTests.Application.Workers;

public sealed class WorkerManagerTests
{
    [Fact]
    public async Task Worker_count_can_be_scaled_and_persisted()
    {
        string connectionString =
            $"Data Source={Guid.NewGuid():N};Mode=Memory;Cache=Shared";
        await using SqliteConnection anchorConnection = new(connectionString);
        await anchorConnection.OpenAsync();

        ServiceCollection services = new();
        services.AddLogging();
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<IOptions<WorkerOptions>>(
            Options.Create(new WorkerOptions
            {
                Count = 1,
                PollIntervalMilliseconds = 50,
                RetryDelaySeconds = 1,
                LeaseGraceSeconds = 5
            }));
        services.AddDbContext<TaskForgeDbContext>(
            options => options.UseSqlite(connectionString));
        services.AddScoped<SqliteJobStore>();
        services.AddScoped<IJobQueue>(
            provider => provider.GetRequiredService<SqliteJobStore>());
        services.AddScoped<IJobRepository>(
            provider => provider.GetRequiredService<SqliteJobStore>());
        services.AddScoped<IWorkerSettingsStore, SqliteWorkerSettingsStore>();
        services.AddSingleton<JobCancellationRegistry>();
        services.AddScoped<JobExecutor>();
        services.AddSingleton<WorkerManager>();

        await using ServiceProvider serviceProvider =
            services.BuildServiceProvider();
        await using (AsyncServiceScope setupScope =
            serviceProvider.CreateAsyncScope())
        {
            TaskForgeDbContext dbContext =
                setupScope.ServiceProvider.GetRequiredService<TaskForgeDbContext>();
            await dbContext.Database.EnsureCreatedAsync();
        }

        WorkerManager manager =
            serviceProvider.GetRequiredService<WorkerManager>();
        await manager.StartAsync(CancellationToken.None);

        try
        {
            await WaitUntilAsync(
                () => manager.GetSnapshot().ActiveWorkerCount == 1);

            await manager.SetWorkerCountAsync(3);
            await WaitUntilAsync(
                () => manager.GetSnapshot().ActiveWorkerCount == 3);

            await manager.SetWorkerCountAsync(0);
            await WaitUntilAsync(
                () => manager.GetSnapshot().Workers.Count == 0);

            await using AsyncServiceScope readScope =
                serviceProvider.CreateAsyncScope();
            IWorkerSettingsStore settingsStore =
                readScope.ServiceProvider
                    .GetRequiredService<IWorkerSettingsStore>();

            Assert.Equal(
                0,
                await settingsStore.GetDesiredWorkerCountAsync());
        }
        finally
        {
            using CancellationTokenSource shutdown = new(
                TimeSpan.FromSeconds(5));
            await manager.StopAsync(shutdown.Token);
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));

        while (!condition())
        {
            await Task.Delay(20, timeout.Token);
        }
    }
}