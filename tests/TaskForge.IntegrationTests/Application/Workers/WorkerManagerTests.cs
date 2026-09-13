using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

using TaskForge.Application.Abstractions.Execution;
using TaskForge.Application.Abstractions.Persistence;
using TaskForge.Application.Workers;
using TaskForge.Infrastructure.Persistence;

namespace TaskForge.IntegrationTests.Application.Workers;

[Collection("SQL Server")]
public sealed class WorkerManagerTests(SqlServerFixture fixture) : SqlServerTest(fixture)
{
    [Fact]
    public async Task Worker_count_can_be_scaled_and_persisted()
    {
        string connectionString = ConnectionString;

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
            options => options.UseSqlServer(connectionString));
        services.AddScoped<EfCoreJobStore>();
        services.AddScoped<IJobQueue>(
            provider => provider.GetRequiredService<EfCoreJobStore>());
        services.AddScoped<IJobRepository>(
            provider => provider.GetRequiredService<EfCoreJobStore>());
        services.AddScoped<IWorkerSettingsStore, EfCoreWorkerSettingsStore>();
        services.AddSingleton<JobCancellationRegistry>();
        services.AddScoped<JobExecutor>();
        services.AddSingleton<WorkerManager>();

        await using ServiceProvider serviceProvider =
            services.BuildServiceProvider();

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

        await using ServiceProvider restartedProvider = services.BuildServiceProvider();
        WorkerManager restartedManager = restartedProvider.GetRequiredService<WorkerManager>();
        await restartedManager.StartAsync(CancellationToken.None);

        try
        {
            Assert.Equal(0, restartedManager.GetSnapshot().DesiredWorkerCount);
            Assert.Equal(0, restartedManager.GetSnapshot().ActiveWorkerCount);
        }
        finally
        {
            using CancellationTokenSource shutdown = new(TimeSpan.FromSeconds(5));
            await restartedManager.StopAsync(shutdown.Token);
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
