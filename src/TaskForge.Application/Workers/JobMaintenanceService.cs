using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using TaskForge.Application.Abstractions.Execution;

namespace TaskForge.Application.Workers;

public sealed class JobMaintenanceService(IServiceScopeFactory scopeFactory, TimeProvider timeProvider, IOptions<WorkerOptions> options, ILogger<JobMaintenanceService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using AsyncServiceScope scope = scopeFactory.CreateAsyncScope();
                IJobQueue queue = scope.ServiceProvider.GetRequiredService<IJobQueue>();
                int recovered = await queue.RecoverExpiredLeasesAsync(timeProvider.GetUtcNow(), stoppingToken);
                if (recovered > 0)
                {
                    logger.LogInformation("Recovered {RecoveredJobCount} job(s) with expired worker leases.", recovered);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Job maintenance failed and will retry.");
            }

            try
            {
                await Task.Delay(TimeSpan.FromMilliseconds(options.Value.PollIntervalMilliseconds), timeProvider, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }
}
