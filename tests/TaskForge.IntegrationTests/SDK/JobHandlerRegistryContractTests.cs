using System.Text.Json;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

using TaskForge.SDK;
using TaskForge.SDK.Handlers;
using TaskForge.SDK.Jobs;

namespace TaskForge.IntegrationTests.SDK;

[Collection("SQL Server")]
public sealed class JobHandlerRegistryContractTests(SqlServerFixture fixture) : SqlServerTest(fixture)
{
    [Fact]
    public async Task Repeated_server_assignments_use_one_client_registry_and_new_scoped_handlers()
    {
        await using WebApplicationFactory<Program> server = CreateServer();
        using HttpClient http = server.CreateClient();
        TaskForgeClient client = new(http, new() { BaseUrl = http.BaseAddress!, ApplicationId = "registry-client" });
        ServiceCollection services = new();
        services.AddSingleton<ExecutionState>();
        services.AddScoped<ExecutionDependency>();
        services.AddTaskForgeHandlers(handlers => handlers.Register<SumPayload, SumHandler>("client-sum"));
        await using ServiceProvider provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
        JobHandlerRegistry registry = provider.GetRequiredService<JobHandlerRegistry>();
        ExecutionState state = provider.GetRequiredService<ExecutionState>();

        for (int i = 0; i < 2; i++)
        {
            JobResponse submitted = await client.SubmitAsync(new("CLIENT-SUM", JsonSerializer.SerializeToElement(new { left = 20, right = 22 + i })));
            Assert.Equal(JobStatus.Queued, submitted.Status);
            ExecutionAssignmentResponse assignment = Assert.IsType<ExecutionAssignmentResponse>(await client.WaitAsync(new("client-worker", registry.RegisteredTypes, 0)));
            Assert.Equal(submitted.Id, assignment.JobId);

            JsonElement? result = await registry.ExecuteAsync(assignment.Type, assignment.Payload);

            Assert.Equal(i + 1, state.Created);
            Assert.Equal(i + 1, state.Disposed);
            JobResponse completed = await client.CompleteAsync(assignment.JobId, assignment.AttemptId, new(assignment.WorkerId, result));
            Assert.Equal(JobStatus.Completed, completed.Status);
            Assert.Equal(42 + i, completed.Result!.Value.GetProperty("sum").GetInt32());
            Assert.Equal(JobAttemptOutcome.Succeeded, Assert.Single(await client.GetAttemptsAsync(submitted.Id)).Outcome);
            Assert.Same(registry, provider.GetRequiredService<JobHandlerRegistry>());
        }

        Assert.Equal(2, state.ScopeIds.Distinct().Count());
    }

    [Fact]
    public async Task Payload_validation_is_client_local_and_the_caller_can_report_it_through_existing_http()
    {
        await using WebApplicationFactory<Program> server = CreateServer();
        using HttpClient http = server.CreateClient();
        TaskForgeClient client = new(http, new() { BaseUrl = http.BaseAddress!, ApplicationId = "registry-client" });
        ServiceCollection services = new();
        services.AddSingleton<ExecutionState>();
        services.AddScoped<ExecutionDependency>();
        services.AddTaskForgeHandlers(handlers => handlers.Register<SumPayload, SumHandler>("client-sum"));
        await using ServiceProvider provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
        JobHandlerRegistry registry = provider.GetRequiredService<JobHandlerRegistry>();
        JobResponse submitted = await client.SubmitAsync(new("client-sum", JsonSerializer.SerializeToElement(new { left = "invalid", right = 22 })));
        ExecutionAssignmentResponse assignment = Assert.IsType<ExecutionAssignmentResponse>(await client.WaitAsync(new("client-worker", registry.RegisteredTypes, 0)));

        await Assert.ThrowsAsync<InvalidJobPayloadException>(() => registry.ExecuteAsync(assignment.Type, assignment.Payload));

        Assert.Equal(0, provider.GetRequiredService<ExecutionState>().Created);
        Assert.Equal(JobStatus.Processing, (await client.GetJobAsync(submitted.Id)).Status);
        JobResponse failed = await client.FailAsync(assignment.JobId, assignment.AttemptId, new(assignment.WorkerId, "InvalidPayload", "The client payload DTO could not be deserialized."));
        Assert.Equal(JobStatus.DeadLettered, failed.Status);
        Assert.Equal(JobAttemptOutcome.PermanentlyFailed, Assert.Single(await client.GetAttemptsAsync(submitted.Id)).Outcome);
    }

    private WebApplicationFactory<Program> CreateServer() => new WebApplicationFactory<Program>().WithWebHostBuilder(builder => builder.UseSetting("ConnectionStrings:TaskForge", ConnectionString));

    public sealed record SumPayload(int Left, int Right);

    public sealed class ExecutionState
    {
        public int Created { get; set; }
        public int Disposed { get; set; }
        public List<Guid> ScopeIds { get; } = [];
    }

    public sealed class ExecutionDependency : IDisposable
    {
        private readonly ExecutionState _state;

        public ExecutionDependency(ExecutionState state)
        {
            _state = state;
            state.Created++;
            state.ScopeIds.Add(Id);
        }

        public Guid Id { get; } = Guid.NewGuid();

        public void Dispose() => _state.Disposed++;
    }

    public sealed class SumHandler(ExecutionDependency dependency) : IJobHandler<SumPayload>
    {
        public Task<JsonElement?> HandleAsync(SumPayload payload, CancellationToken cancellationToken = default)
        {
            return Task.FromResult<JsonElement?>(JsonSerializer.SerializeToElement(new { sum = payload.Left + payload.Right, scopeId = dependency.Id }));
        }
    }
}
