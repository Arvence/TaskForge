using System.Text.Json;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

using TaskForge.Cli;
using TaskForge.SampleClient.Handlers;
using TaskForge.SDK.Handlers;

namespace TaskForge.SampleClient;

public static class SampleHandlers
{
    public static IServiceCollection AddSampleHandlers(this IServiceCollection services, IConfiguration configuration)
    {
        AddHandlerDependencies(services, configuration);
        return services.AddTaskForgeHandlers(handlers => handlers
            .Register<JsonElement, ReportExecutionHandler>("generate-report")
            .Register<JsonElement, HttpRequestJobHandler>("http-request"));
    }

    public static IServiceCollection AddSampleOperationWorker(this IServiceCollection services, IConfiguration configuration)
    {
        AddHandlerDependencies(services, configuration);
        return services.AddTaskForgeCliWorker(configuration, operations => operations
            .Register<GenerateReportPayload, ReportExecutionHandler>("generate-report")
            .Register<HttpRequestPayload, HttpRequestJobHandler>("http-request"));
    }

    private static void AddHandlerDependencies(IServiceCollection services, IConfiguration configuration)
    {
        IConfigurationSection httpJobs = configuration.GetSection(HttpRequestJobOptions.SectionName);
        if (httpJobs.GetSection("AllowedHosts").Exists())
        {
            throw new InvalidOperationException("Replace HttpRequestJobs:AllowedHosts with HttpRequestJobs:AllowedOrigin.");
        }

        services.Configure<HttpRequestJobOptions>(httpJobs);
        services.AddScoped<GenerateReportJobHandler>();
        services.AddHttpClient<HttpRequestJobHandler>(client => client.Timeout = Timeout.InfiniteTimeSpan)
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false });
    }
}
