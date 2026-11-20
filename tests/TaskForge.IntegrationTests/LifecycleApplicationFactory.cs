using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using TaskForge.Api.Jobs;
using TaskForge.Application.Jobs.Validation;
using TaskForge.Domain.Jobs;

namespace TaskForge.IntegrationTests;

internal sealed class LifecycleApplicationFactory : WebApplicationFactory<Program>
{
    private static readonly string[] Applications = ["test-app", "a-project", "b-project", "sdk-project", "registry-client", "client-app", "other-app", new string('a', 100)];
    private static readonly Dictionary<string, string> Keys = Applications.ToDictionary(id => id, _ => Convert.ToHexString(RandomNumberGenerator.GetBytes(32)));

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        foreach ((string applicationId, string key) in Keys)
        {
            builder.UseSetting($"JobAccess:Applications:{applicationId}:ApiKey", key);
        }

        builder.ConfigureTestServices(services =>
            services.Replace(ServiceDescriptor.Singleton<IJobPayloadPolicy, TestPayloadPolicy>()));
    }

    protected override void ConfigureClient(HttpClient client)
    {
        base.ConfigureClient(client);
        SetApplication(client);
    }

    public static string KeyFor(string applicationId) => Keys[JobApplicationId.Normalize(applicationId)];

    public static void SetApplication(HttpClient client, string applicationId = "test-app") =>
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", KeyFor(applicationId));

    public static HttpClient CreateExecutionClient(WebApplication app, string applicationId = "test-app")
    {
        HttpClient client = app.GetTestClient();
        SetApplication(client, applicationId);
        return client;
    }

    public static RouteGroupBuilder ProtectRoutes(WebApplication app)
    {
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(Keys.Select(entry => new KeyValuePair<string, string?>($"JobAccess:Applications:{entry.Key}:ApiKey", entry.Value)))
            .Build();
        app.UseRouting();
        app.Use(new JobAccess(configuration).AuthorizeAsync);
        return app.MapGroup(string.Empty).WithMetadata(new RequiresApplicationKey());
    }

    private sealed class TestPayloadPolicy : IJobPayloadPolicy
    {
        public string? Validate(string applicationId, string jobType, JsonElement payload, int payloadBytes) => null;
    }
}
