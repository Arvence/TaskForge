using System.Collections.Concurrent;
using System.Text.Json;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

using TaskForge.SDK;
using TaskForge.SDK.Execution;
using TaskForge.SDK.Handlers;
using TaskForge.SDK.Jobs;

namespace TaskForge.IntegrationTests.SDK;

[Collection("SQL Server")]
public sealed class TaskForgeWorkerContractTests(SqlServerFixture fixture) : SqlServerTest(fixture)
{
    [Fact]
    public async Task Hosted_worker_repeats_assignments_and_leaves_other_applications_and_types_queued()
    {
        await using WebApplicationFactory<Program> server = CreateServer();
        using HttpClient http = server.CreateClient();
        TaskForgeClient client = Client(http, "a-project");
        TaskForgeClient otherClient = Client(http, "b-project");
        JobResponse first = await client.SubmitAsync(Request("first"));
        JobResponse second = await client.SubmitAsync(Request("second"));
        JobResponse foreign = await otherClient.SubmitAsync(Request("foreign"));
        JobResponse unsupported = await client.SubmitAsync(Request("unsupported") with { Type = "another-type" });
        WorkerState state = new();
        using IHost host = CreateClientHost(client, state);
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(30));

        await host.StartAsync(deadline.Token);
        try
        {
            JobResponse firstCompleted = await WaitForTerminalAsync(client, first.Id, deadline.Token);
            JobResponse secondCompleted = await WaitForTerminalAsync(client, second.Id, deadline.Token);
            Assert.Equal(JobStatus.Completed, firstCompleted.Status);
            Assert.Equal(JobStatus.Completed, secondCompleted.Status);
            Assert.Equal(42, firstCompleted.Result!.Value.GetProperty("value").GetInt32());
            Assert.Equal(42, secondCompleted.Result!.Value.GetProperty("value").GetInt32());
            JobAttemptResponse firstAttempt = Assert.Single(await client.GetAttemptsAsync(first.Id, deadline.Token));
            JobAttemptResponse secondAttempt = Assert.Single(await client.GetAttemptsAsync(second.Id, deadline.Token));
            Assert.Equal(JobAttemptOutcome.Succeeded, firstAttempt.Outcome);
            Assert.Equal(JobAttemptOutcome.Succeeded, secondAttempt.Outcome);
            Assert.NotEqual(firstAttempt.AttemptId, secondAttempt.AttemptId);
            Assert.Equal(firstAttempt.WorkerId, secondAttempt.WorkerId);
            Assert.StartsWith("taskforge-", firstAttempt.WorkerId);
            Assert.Equal(JobStatus.Queued, (await otherClient.GetJobAsync(foreign.Id, deadline.Token)).Status);
            Assert.Empty(await otherClient.GetAttemptsAsync(foreign.Id, deadline.Token));
            Assert.Equal(JobStatus.Queued, (await client.GetJobAsync(unsupported.Id, deadline.Token)).Status);
            Assert.Empty(await client.GetAttemptsAsync(unsupported.Id, deadline.Token));
            Assert.Equal(2, state.Disposed);
            Assert.Equal(2, state.ScopeIds.Distinct().Count());
            Assert.All(state.Calls.Values, calls => Assert.Equal(1, calls));
        }
        finally
        {
            await host.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    public async Task Server_assigns_retry_after_failure_and_applies_payload_result_and_permanent_failure_codes()
    {
        await using WebApplicationFactory<Program> server = CreateServer();
        using HttpClient http = server.CreateClient();
        TaskForgeClient client = Client(http, "a-project");
        JobResponse retry = await client.SubmitAsync(Request("retry") with { MaxRetries = 1 });
        JobResponse invalid = await client.SubmitAsync(new("sdk-work", JsonSerializer.SerializeToElement(new { name = "invalid", value = "invalid" })));
        JobResponse resultFailure = await client.SubmitAsync(Request("bad-result") with { MaxRetries = 0 });
        JobResponse permanent = await client.SubmitAsync(Request("permanent"));
        WorkerState state = new();
        using IHost host = CreateClientHost(client, state);
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(30));

        await host.StartAsync(deadline.Token);
        try
        {
            JobResponse completed = await WaitForTerminalAsync(client, retry.Id, deadline.Token);
            Assert.Equal(JobStatus.Completed, completed.Status);
            Assert.Equal(1, completed.RetryCount);
            Assert.Equal(42, completed.Result!.Value.GetProperty("value").GetInt32());
            IReadOnlyList<JobAttemptResponse> attempts = await client.GetAttemptsAsync(retry.Id, deadline.Token);
            Assert.Equal([1, 2], attempts.Select(attempt => attempt.AttemptNumber));
            Assert.Equal(2, attempts.Select(attempt => attempt.AttemptId).Distinct().Count());
            Assert.Equal(JobAttemptOutcome.Failed, attempts[0].Outcome);
            Assert.Equal("invalidoperationexception", attempts[0].ErrorCode);
            Assert.Equal(JobAttemptOutcome.Succeeded, attempts[1].Outcome);
            Assert.True(attempts[1].StartedAtUtc >= attempts[0].FinishedAtUtc!.Value.AddSeconds(1));
            Assert.Equal(attempts[0].WorkerId, attempts[1].WorkerId);
            Assert.Equal(2, state.Calls["retry"]);

            foreach ((Guid id, string code, JobAttemptOutcome outcome) in new[]
            {
                (invalid.Id, "invalidpayload", JobAttemptOutcome.PermanentlyFailed),
                (resultFailure.Id, "resultserializationfailed", JobAttemptOutcome.Failed),
                (permanent.Id, "nonretryablejobexception", JobAttemptOutcome.PermanentlyFailed)
            })
            {
                JobResponse failed = await WaitForTerminalAsync(client, id, deadline.Token);
                Assert.Equal(JobStatus.DeadLettered, failed.Status);
                JobAttemptResponse attempt = Assert.Single(await client.GetAttemptsAsync(id, deadline.Token));
                Assert.Equal(code, attempt.ErrorCode);
                Assert.Equal(outcome, attempt.Outcome);
            }

            Assert.False(state.Calls.ContainsKey("invalid"));
            Assert.Equal(4, state.Disposed);
            Assert.Equal(4, state.ScopeIds.Distinct().Count());
        }
        finally
        {
            await host.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    private WebApplicationFactory<Program> CreateServer() => new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        builder.UseSetting("ConnectionStrings:TaskForge", ConnectionString).UseSetting("Worker:RetryDelaySeconds", "1"));

    private static TaskForgeClient Client(HttpClient http, string applicationId) => new(http, new() { BaseUrl = http.BaseAddress!, ApplicationId = applicationId });

    private static SubmitJobRequest Request(string name) => new("sdk-work", JsonSerializer.SerializeToElement(new { name, value = 21 }));

    private static IHost CreateClientHost(TaskForgeClient client, WorkerState state) => new HostBuilder().ConfigureServices(services =>
    {
        services.AddSingleton(client);
        services.AddSingleton(state);
        services.AddScoped<ExecutionDependency>();
        services.AddTaskForgeHandlers(registry => registry.Register<WorkPayload, WorkHandler>("sdk-work"));
        services.AddTaskForgeWorker(new() { WaitSeconds = 1 });
    }).Build();

    private static async Task<JobResponse> WaitForTerminalAsync(TaskForgeClient client, Guid jobId, CancellationToken cancellationToken)
    {
        while (true)
        {
            JobResponse job = await client.GetJobAsync(jobId, cancellationToken);
            if (job.Status is JobStatus.Completed or JobStatus.DeadLettered)
            {
                return job;
            }

            await Task.Delay(50, cancellationToken);
        }
    }

    public sealed record WorkPayload(string Name, int Value);

    public sealed class WorkerState
    {
        public ConcurrentDictionary<string, int> Calls { get; } = new();
        public ConcurrentQueue<Guid> ScopeIds { get; } = new();
        public int Disposed;
    }

    public sealed class ExecutionDependency : IDisposable
    {
        private readonly WorkerState _state;

        public ExecutionDependency(WorkerState state)
        {
            _state = state;
            state.ScopeIds.Enqueue(Id);
        }

        public Guid Id { get; } = Guid.NewGuid();

        public void Dispose() => Interlocked.Increment(ref _state.Disposed);
    }

    public sealed class WorkHandler(WorkerState state, ExecutionDependency dependency) : IJobHandler<WorkPayload>
    {
        public Task<JsonElement?> HandleAsync(WorkPayload payload, CancellationToken cancellationToken = default)
        {
            int calls = state.Calls.AddOrUpdate(payload.Name, 1, (_, count) => count + 1);
            if (payload.Name == "retry" && calls == 1)
            {
                throw new InvalidOperationException("Temporary client dependency failure.");
            }

            if (payload.Name == "permanent")
            {
                throw new NonRetryableJobException("The business request cannot be fulfilled.");
            }

            return Task.FromResult<JsonElement?>(payload.Name == "bad-result"
                ? default(JsonElement)
                : JsonSerializer.SerializeToElement(new { value = payload.Value * 2, scopeId = dependency.Id }));
        }
    }
}
