using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using TaskForge.SDK;
using TaskForge.SDK.Execution;
using TaskForge.SDK.Handlers;

namespace TaskForge.Cli;

public static class CliWorkerServiceCollectionExtensions
{
    public static IServiceCollection AddTaskForgeCliWorker(this IServiceCollection services, IConfiguration configuration, Action<JobHandlerRegistryBuilder>? configureOperations = null, IReadOnlyCollection<string>? supportedTypes = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        IConfigurationSection settings = configuration.GetSection("TaskForge");
        services.AddTaskForgeHandlers(configureOperations ?? (_ => { }), supportedTypes ?? ["general"], JobRouter.SelectOperation, JobRouter.ParameterOptions);

        services.TryAddSingleton(_ => new HttpClient { Timeout = Timeout.InfiniteTimeSpan });
        services.AddSingleton(provider => new TaskForgeClient(provider.GetRequiredService<HttpClient>(), new()
        {
            BaseUrl = new Uri(settings["BaseUrl"] ?? "http://localhost:8275", UriKind.Absolute),
            ApplicationId = RequiredSetting(settings, "ApplicationId"),
            ApiKey = RequiredSetting(settings, "ApiKey")
        }));
        services.AddTaskForgeWorker(new() { SlotCount = settings.GetValue("Worker:SlotCount", 1) });
        return services;
    }

    private static string RequiredSetting(IConfigurationSection settings, string name) =>
        settings[name] is { } value && !string.IsNullOrWhiteSpace(value)
            ? value
            : throw new InvalidOperationException($"Configure {settings.Path}:{name} before running the worker.");
}
