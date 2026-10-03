using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

using TaskForge.SampleClient.Handlers;
using TaskForge.SDK.Handlers;

namespace TaskForge.SampleClient;

public static class SampleHandlers
{
    public static IServiceCollection AddSampleHandlers(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<HttpRequestJobOptions>(configuration.GetSection(HttpRequestJobOptions.SectionName));
        services.AddScoped<GenerateReportJobHandler>();
        services.AddHttpClient<HttpRequestJobHandler>(client => client.Timeout = Timeout.InfiniteTimeSpan)
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false });
        services.AddTaskForgeHandlers(handlers => handlers
            .Register<GenerateReportPayload, ReportExecutionHandler>("generate-report")
            .Register<HttpRequestPayload, HttpRequestJobHandler>("http-request"));
        return services;
    }
}
