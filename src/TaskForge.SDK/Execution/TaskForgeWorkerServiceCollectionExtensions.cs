using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace TaskForge.SDK.Execution;

public static class TaskForgeWorkerServiceCollectionExtensions
{
    public static IServiceCollection AddTaskForgeWorker(this IServiceCollection services, TaskForgeWorkerOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        if (services.Any(descriptor => descriptor.ServiceType == typeof(IHostedService) && descriptor.ImplementationType == typeof(TaskForgeWorker)))
        {
            throw new InvalidOperationException("The TaskForge worker is already registered.");
        }

        options ??= new TaskForgeWorkerOptions();
        options.Validate();
        services.AddSingleton(options);
        services.AddHostedService<TaskForgeWorker>();
        return services;
    }
}
