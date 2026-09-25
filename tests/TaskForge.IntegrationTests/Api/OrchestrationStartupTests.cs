using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

using TaskForge.Api.Jobs;
using TaskForge.Application.Abstractions.Execution;
using TaskForge.Application.Abstractions.Persistence;
using TaskForge.Application.Jobs;
using TaskForge.Application.Workers;
using TaskForge.Domain.Jobs;
using TaskForge.Infrastructure.Jobs.Handlers;
using TaskForge.Infrastructure.Persistence;

namespace TaskForge.IntegrationTests.Api;

[Collection("SQL Server")]
public sealed class OrchestrationStartupTests(SqlServerFixture fixture) : SqlServerTest(fixture)
{
    [Fact]
    public async Task Startup_ignores_legacy_execution_settings_and_registers_only_orchestration()
    {
        await using TaskForgeDbContext context = new(DatabaseOptions);
        await new EfCoreWorkerSettingsStore(context).SetDesiredWorkerCountAsync(8, DateTimeOffset.UtcNow);
        await using WebApplicationFactory<Program> factory = CreateFactory().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Worker:Count", "not-an-integer");
            builder.UseSetting("HttpRequestJobs:AllowedHosts:0", " ");
        });
        using HttpClient client = factory.CreateClient();
        await using AsyncServiceScope scope = factory.Services.CreateAsyncScope();
        IServiceProvider services = scope.ServiceProvider;

        Assert.Empty(services.GetServices<IJobHandler>());
        Assert.Null(services.GetService<HttpRequestJobHandler>());
        Assert.Null(services.GetService<GenerateReportJobHandler>());
        Assert.Null(services.GetService<JobExecutor>());
        Assert.Null(services.GetService<WorkerManager>());
        Assert.Null(services.GetService<JobCancellationRegistry>());
        Assert.Null(services.GetService<IWorkerSettingsStore>());
        Assert.NotNull(services.GetRequiredService<JobExecutionService>());
        Assert.NotNull(services.GetRequiredService<JobDistributionService>());
        IHostedService[] hosted = services.GetServices<IHostedService>().ToArray();
        Assert.Single(hosted.OfType<JobMaintenanceService>());
        Assert.Empty(hosted.OfType<WorkerManager>());
        WorkerOptions options = services.GetRequiredService<IOptions<WorkerOptions>>().Value;
        Assert.Equal(50, options.PollIntervalMilliseconds);
        Assert.Equal(5, options.RetryDelaySeconds);
        Assert.Equal(300, options.MaxRetryDelaySeconds);
        Assert.Equal(30, options.LeaseGraceSeconds);
        using HttpResponseMessage health = await client.GetAsync("/api/health");
        using HttpResponseMessage readiness = await client.GetAsync("/api/ready");
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
        Assert.Equal(HttpStatusCode.OK, readiness.StatusCode);
        Assert.Equal(8, await new EfCoreWorkerSettingsStore(context).GetDesiredWorkerCountAsync());
    }

    [Theory]
    [InlineData("send-email")]
    [InlineData("client-specific-job")]
    [InlineData("http-request")]
    [InlineData("generate-report")]
    public async Task Valid_jobs_remain_queued_without_handlers_until_an_external_client_requests_them(string type)
    {
        await using WebApplicationFactory<Program> factory = CreateFactory();
        using HttpClient client = factory.CreateClient();
        using HttpResponseMessage submitted = await client.PostAsJsonAsync("/api/jobs",
            new { applicationId = "client-app", type, payload = new { value = 1 } });
        Assert.Equal(HttpStatusCode.Created, submitted.StatusCode);
        Guid id = (await submitted.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        await Task.Delay(250);
        await using (TaskForgeDbContext read = new(DatabaseOptions))
        {
            Assert.Equal(JobStatus.Queued, (await read.Jobs.SingleAsync()).Status);
            Assert.Empty(await read.JobAttempts.ToListAsync());
        }

        using HttpResponseMessage acquired = await client.PostAsJsonAsync("/api/executions/wait",
            new { applicationId = "client-app", workerId = "external-client", supportedTypes = new[] { type }, waitSeconds = 0 });
        Assert.Equal(HttpStatusCode.OK, acquired.StatusCode);
        ExecutionAssignmentResponse assignment = (await acquired.Content.ReadFromJsonAsync<ExecutionAssignmentResponse>())!;
        Assert.Equal(id, assignment.JobId);
        Assert.Equal(type, assignment.Type);
        Assert.Equal(1, assignment.Payload.GetProperty("value").GetInt32());
        await using TaskForgeDbContext persisted = new(DatabaseOptions);
        Assert.Equal(JobStatus.Processing, (await persisted.Jobs.SingleAsync()).Status);
        Assert.Equal(JobAttemptOutcome.Running, (await persisted.JobAttempts.SingleAsync()).Outcome);
    }

    [Theory]
    [InlineData(0, JobStatus.DeadLettered)]
    [InlineData(2, JobStatus.Retrying)]
    public async Task Maintenance_resolves_expired_execution_without_a_connected_client(int maxRetries, JobStatus expectedStatus)
    {
        DateTimeOffset start = DateTimeOffset.UtcNow.AddMinutes(-2);
        Job job = new(Guid.NewGuid(), "client-app", "send-email", "{}", JobPriority.Normal, maxRetries, 1, start);
        job.Queue(start);
        await using (TaskForgeDbContext seed = new(DatabaseOptions))
        {
            EfCoreJobStore store = new(seed);
            await store.AddAsync(job);
            Assert.NotNull(await store.TryDistributeAsync("client-app", "disconnected-client", ["send-email"], TimeSpan.FromSeconds(30), start));
        }

        await using WebApplicationFactory<Program> factory = CreateFactory();
        using HttpClient client = factory.CreateClient();
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(15));
        while (true)
        {
            await using TaskForgeDbContext read = new(DatabaseOptions);
            Job persisted = await read.Jobs.SingleAsync(deadline.Token);
            if (persisted.Status != JobStatus.Processing)
            {
                Assert.Equal(expectedStatus, persisted.Status);
                Assert.Equal(1, persisted.RetryCount);
                Assert.Null(persisted.OwningWorkerId);
                Assert.Null(persisted.LeaseExpiresAtUtc);
                Assert.Equal(JobAttemptOutcome.TimedOut, (await read.JobAttempts.SingleAsync(deadline.Token)).Outcome);
                break;
            }

            await Task.Delay(20, deadline.Token);
        }
    }

    [Theory]
    [InlineData("GET", "/api/workers", null)]
    [InlineData("PUT", "/api/workers/count", "{\"count\":8}")]
    [InlineData("PUT", "/api/workers/count", "{\"count\":-1}")]
    [InlineData("PUT", "/api/workers/count", "{")]
    [InlineData("PUT", "/api/workers/count", null)]
    public async Task Retired_worker_routes_return_410_without_binding_or_persisting_settings(string method, string route, string? body)
    {
        await using WebApplicationFactory<Program> factory = CreateFactory();
        using HttpClient client = factory.CreateClient();
        using HttpRequestMessage request = new(new HttpMethod(method), route);
        if (body is not null)
        {
            request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        }

        using HttpResponseMessage response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Gone, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        JsonElement problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(410, problem.GetProperty("status").GetInt32());
        Assert.Contains("retired", problem.GetProperty("detail").GetString());
        await using TaskForgeDbContext context = new(DatabaseOptions);
        Assert.Null(await new EfCoreWorkerSettingsStore(context).GetDesiredWorkerCountAsync());
        using HttpResponseMessage openApi = await client.GetAsync("/openapi/v1.json");
        JsonElement paths = (await openApi.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("paths");
        Assert.True(paths.GetProperty(route).GetProperty(method.ToLowerInvariant()).GetProperty("responses").TryGetProperty("410", out _));
    }

    private WebApplicationFactory<Program> CreateFactory() => new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
    {
        builder.UseSetting("ConnectionStrings:TaskForge", ConnectionString);
        builder.UseSetting("Worker:PollIntervalMilliseconds", "50");
    });
}
