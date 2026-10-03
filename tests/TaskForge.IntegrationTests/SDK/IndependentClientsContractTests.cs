using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

using TaskForge.SampleClient;
using TaskForge.SampleClient.Handlers;
using TaskForge.SDK;
using TaskForge.SDK.Execution;
using TaskForge.SDK.Handlers;
using TaskForge.SDK.Jobs;

namespace TaskForge.IntegrationTests.SDK;

[Collection("SQL Server")]
public sealed class IndependentClientsContractTests(SqlServerFixture fixture) : SqlServerTest(fixture)
{
    [Fact]
    public async Task Same_named_handlers_in_independent_applications_use_only_their_local_behavior()
    {
        await using WebApplicationFactory<global::Program> server = CreateServer();
        WaitObserver trafficA = new();
        WaitObserver trafficB = new();
        using HttpClient httpA = server.CreateDefaultClient(trafficA);
        using HttpClient httpB = server.CreateDefaultClient(trafficB);
        TaskForgeClient clientA = Client(httpA, "a-project");
        TaskForgeClient clientB = Client(httpB, "b-project");
        ReportState stateA = new("A-local-report");
        ReportState stateB = new("B-local-report");
        using IHost hostA = ReportHost(clientA, stateA);
        using IHost hostB = ReportHost(clientB, stateB);
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(30));
        await hostA.StartAsync(deadline.Token);
        try
        {
            JobResponse jobB = await SampleJobs.SubmitReportAsync(clientB, SampleJobs.ExampleReport(), deadline.Token);
            int before = Volatile.Read(ref trafficA.EmptyWaits);
            await WaitUntilAsync(() => Volatile.Read(ref trafficA.EmptyWaits) >= before + 2, deadline.Token);
            Assert.Equal(JobStatus.Queued, (await clientB.GetJobAsync(jobB.Id, deadline.Token)).Status);
            Assert.Empty(await clientB.GetAttemptsAsync(jobB.Id, deadline.Token));
            Assert.Empty(stateA.Calls);

            await hostB.StartAsync(deadline.Token);
            JobResponse jobA = await SampleJobs.SubmitReportAsync(clientA, SampleJobs.ExampleReport(), deadline.Token);
            JobResponse resultA = await WaitForCompletedAsync(clientA, jobA.Id, deadline.Token);
            JobResponse resultB = await WaitForCompletedAsync(clientB, jobB.Id, deadline.Token);
            Assert.Equal("A-local-report", resultA.Result!.Value.GetProperty("client").GetString());
            Assert.Equal("B-local-report", resultB.Result!.Value.GetProperty("client").GetString());
            Assert.Equal(190.25m, resultA.Result.Value.GetProperty("report").GetProperty("totalAmount").GetDecimal());
            Assert.Equal(resultA.Result.Value.GetProperty("report").GetRawText(), resultB.Result.Value.GetProperty("report").GetRawText());
            AssertContext(Assert.Single(stateA.Calls), jobA, Assert.Single(await clientA.GetAttemptsAsync(jobA.Id, deadline.Token)));
            AssertContext(Assert.Single(stateB.Calls), jobB, Assert.Single(await clientB.GetAttemptsAsync(jobB.Id, deadline.Token)));
        }
        finally
        {
            await StopAsync(hostA, hostB);
        }
    }

    [Fact]
    public async Task Two_competing_clients_for_one_application_execute_one_assignment_only_once()
    {
        await using WebApplicationFactory<global::Program> server = CreateServer();
        WaitObserver firstTraffic = new();
        WaitObserver secondTraffic = new();
        using HttpClient firstHttp = server.CreateDefaultClient(firstTraffic);
        using HttpClient secondHttp = server.CreateDefaultClient(secondTraffic);
        TaskForgeClient client = Client(firstHttp, "a-project");
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        ReportState first = new("first-instance", release);
        ReportState second = new("second-instance", release);
        using IHost firstHost = ReportHost(client, first);
        using IHost secondHost = ReportHost(Client(secondHttp, "a-project"), second);
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(30));
        await Task.WhenAll(firstHost.StartAsync(deadline.Token), secondHost.StartAsync(deadline.Token));
        try
        {
            await WaitUntilAsync(() => Volatile.Read(ref firstTraffic.EmptyWaits) > 0 && Volatile.Read(ref secondTraffic.EmptyWaits) > 0, deadline.Token);
            JobResponse submitted = await SampleJobs.SubmitReportAsync(client, SampleJobs.ExampleReport(), deadline.Token);
            Task<JobExecutionContext> entered = await Task.WhenAny(first.Entered.Task, second.Entered.Task).WaitAsync(deadline.Token);
            JobExecutionContext owner = await entered;
            WaitObserver competitor = first.Entered.Task.IsCompleted ? secondTraffic : firstTraffic;
            int before = Volatile.Read(ref competitor.EmptyWaits);
            await WaitUntilAsync(() => Volatile.Read(ref competitor.EmptyWaits) >= before + 2, deadline.Token);
            Assert.Equal(1, first.Calls.Count + second.Calls.Count);
            JobAttemptResponse running = Assert.Single(await client.GetAttemptsAsync(submitted.Id, deadline.Token));
            Assert.Equal(JobAttemptOutcome.Running, running.Outcome);
            AssertContext(owner, submitted, running);

            release.TrySetResult();
            JobResponse completed = await WaitForCompletedAsync(client, submitted.Id, deadline.Token);
            Assert.Equal(0, completed.RetryCount);
            Assert.Equal(JobAttemptOutcome.Succeeded, Assert.Single(await client.GetAttemptsAsync(submitted.Id, deadline.Token)).Outcome);
            Assert.Equal(1, first.Calls.Count + second.Calls.Count);
        }
        finally
        {
            release.TrySetResult();
            await StopAsync(firstHost, secondHost);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Receiver_test_double_protects_repeated_side_effects_only_when_the_business_key_survives_attempt_changes(bool useLogicalJobKey)
    {
        await using WebApplicationFactory<global::Program> server = CreateServer();
        using HttpClient http = server.CreateClient();
        TaskForgeClient client = Client(http, "a-project");
        SideEffectReceiver receiver = new();
        EffectState interrupted = new(useLogicalJobKey, blockAfterEffect: true);
        EffectState replacement = new(useLogicalJobKey, blockAfterEffect: false);
        using IHost firstHost = EffectHost(client, receiver, interrupted);
        using IHost secondHost = EffectHost(client, receiver, replacement);
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(30));
        JobResponse job = await client.SubmitAsync(new("record-effect", JsonSerializer.SerializeToElement(new EffectPayload("test-operation")), MaxRetries: 1, TimeoutSeconds: 3), cancellationToken: deadline.Token);
        await firstHost.StartAsync(deadline.Token);
        try
        {
            JobExecutionContext first = await interrupted.Entered.Task.WaitAsync(deadline.Token);
            Assert.Equal(1, receiver.EffectCount);
            await firstHost.StopAsync(deadline.Token);
            await secondHost.StartAsync(deadline.Token);
            JobResponse completed = await WaitForCompletedAsync(client, job.Id, deadline.Token);
            JobExecutionContext second = await replacement.Entered.Task.WaitAsync(deadline.Token);
            IReadOnlyList<JobAttemptResponse> history = await client.GetAttemptsAsync(job.Id, deadline.Token);
            Assert.Equal([JobAttemptOutcome.TimedOut, JobAttemptOutcome.Succeeded], history.Select(attempt => attempt.Outcome));
            Assert.Equal(1, completed.RetryCount);
            Assert.Equal(first.JobId, second.JobId);
            Assert.NotEqual(first.AttemptId, second.AttemptId);
            Assert.NotEqual(first.WorkerId, second.WorkerId);
            Assert.Equal(2, second.AttemptNumber);
            AssertContext(first, job, history[0]);
            AssertContext(second, job, history[1]);
            Assert.Equal(2, receiver.CallCount);
            Assert.Equal(useLogicalJobKey ? 1 : 2, receiver.EffectCount);
            Assert.Equal(useLogicalJobKey ? 1 : 2, completed.Result!.Value.GetProperty("receipt").GetInt32());

            JobResponse separateJob = await client.SubmitAsync(new("record-effect", JsonSerializer.SerializeToElement(new EffectPayload("test-operation"))), cancellationToken: deadline.Token);
            await WaitForCompletedAsync(client, separateJob.Id, deadline.Token);
            Assert.NotEqual(job.Id, separateJob.Id);
            Assert.Equal(useLogicalJobKey ? 2 : 3, receiver.EffectCount);
            Assert.Equal(3, receiver.CallCount);
        }
        finally
        {
            await StopAsync(firstHost, secondHost);
        }
    }

    private WebApplicationFactory<global::Program> CreateServer() => new WebApplicationFactory<global::Program>().WithWebHostBuilder(builder => builder
        .UseSetting("ConnectionStrings:TaskForge", ConnectionString).UseSetting("Worker:PollIntervalMilliseconds", "50").UseSetting("Worker:RetryDelaySeconds", "1"));

    private static TaskForgeClient Client(HttpClient http, string applicationId) => new(http, new() { BaseUrl = http.BaseAddress!, ApplicationId = applicationId });

    private static IHost ReportHost(TaskForgeClient client, ReportState state) => new HostBuilder().ConfigureServices(services =>
    {
        services.AddSingleton(client);
        services.AddSingleton(state);
        services.AddTaskForgeHandlers(handlers => handlers.Register<GenerateReportPayload, LocalReportHandler>("generate-report"));
        services.AddTaskForgeWorker(new() { WaitSeconds = 0, NoWorkDelay = TimeSpan.FromMilliseconds(20) });
    }).Build();

    private static IHost EffectHost(TaskForgeClient client, SideEffectReceiver receiver, EffectState state) => new HostBuilder().ConfigureServices(services =>
    {
        services.AddSingleton(client);
        services.AddSingleton(receiver);
        services.AddSingleton(state);
        services.AddTaskForgeHandlers(handlers => handlers.Register<EffectPayload, EffectHandler>("record-effect"));
        services.AddTaskForgeWorker(new() { WaitSeconds = 1, StatusPollInterval = TimeSpan.FromMilliseconds(50) });
    }).Build();

    private static async Task StopAsync(params IHost[] hosts) => await Task.WhenAll(hosts.Select(host => host.StopAsync(CancellationToken.None))).WaitAsync(TimeSpan.FromSeconds(5));

    private static async Task WaitUntilAsync(Func<bool> condition, CancellationToken cancellationToken)
    {
        while (!condition())
        {
            await Task.Delay(20, cancellationToken);
        }
    }

    private static async Task<JobResponse> WaitForCompletedAsync(TaskForgeClient client, Guid jobId, CancellationToken cancellationToken)
    {
        while (true)
        {
            JobResponse job = await client.GetJobAsync(jobId, cancellationToken);
            if (job.Status == JobStatus.Completed)
            {
                return job;
            }

            Assert.NotEqual(JobStatus.DeadLettered, job.Status);
            await Task.Delay(20, cancellationToken);
        }
    }

    private static void AssertContext(JobExecutionContext context, JobResponse job, JobAttemptResponse attempt)
    {
        Assert.Equal(job.Id, context.JobId);
        Assert.Equal(job.ApplicationId, context.ApplicationId);
        Assert.Equal(job.Type, context.JobType);
        Assert.Equal(attempt.AttemptId, context.AttemptId);
        Assert.Equal(attempt.AttemptNumber, context.AttemptNumber);
        Assert.Equal(attempt.WorkerId, context.WorkerId);
        Assert.Equal(attempt.StartedAtUtc, context.StartedAtUtc);
        Assert.Equal(attempt.DeadlineAtUtc, context.DeadlineAtUtc);
    }

    private sealed class WaitObserver : DelegatingHandler
    {
        public int EmptyWaits;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            HttpResponseMessage response = await base.SendAsync(request, cancellationToken);
            if (request.RequestUri!.AbsolutePath == "/api/executions/wait" && response.StatusCode == HttpStatusCode.NoContent)
            {
                Interlocked.Increment(ref EmptyWaits);
            }

            return response;
        }
    }

    public sealed class ReportState(string label, TaskCompletionSource? release = null)
    {
        public string Label { get; } = label;
        public Task Gate { get; } = release?.Task ?? Task.CompletedTask;
        public TaskCompletionSource<JobExecutionContext> Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ConcurrentQueue<JobExecutionContext> Calls { get; } = new();
    }

    public sealed class LocalReportHandler(ReportState state, JobExecutionContext context) : IJobHandler<GenerateReportPayload>
    {
        public async Task<JsonElement?> HandleAsync(GenerateReportPayload payload, CancellationToken cancellationToken = default)
        {
            state.Calls.Enqueue(context);
            state.Entered.TrySetResult(context);
            await state.Gate.WaitAsync(cancellationToken);
            JsonElement? report = await new GenerateReportJobHandler().HandleAsync(payload, cancellationToken);
            return JsonSerializer.SerializeToElement(new { client = state.Label, report });
        }
    }

    public sealed record EffectPayload(string Operation);

    public sealed class EffectState(bool useLogicalJobKey, bool blockAfterEffect)
    {
        public bool UseLogicalJobKey { get; } = useLogicalJobKey;
        public bool BlockAfterEffect { get; } = blockAfterEffect;
        public TaskCompletionSource<JobExecutionContext> Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    public sealed class SideEffectReceiver
    {
        private readonly Dictionary<string, int> _receipts = new(StringComparer.Ordinal);
        public int EffectCount { get; private set; }
        public int CallCount { get; private set; }

        public int Apply(string key)
        {
            lock (_receipts)
            {
                CallCount++;
                if (!_receipts.TryGetValue(key, out int receipt))
                {
                    receipt = ++EffectCount;
                    _receipts.Add(key, receipt);
                }

                return receipt;
            }
        }
    }

    public sealed class EffectHandler(SideEffectReceiver receiver, EffectState state, JobExecutionContext context) : IJobHandler<EffectPayload>
    {
        public async Task<JsonElement?> HandleAsync(EffectPayload payload, CancellationToken cancellationToken = default)
        {
            string key = $"{context.ApplicationId}:{payload.Operation}:{(state.UseLogicalJobKey ? context.JobId : context.AttemptId):D}";
            int receipt = receiver.Apply(key);
            state.Entered.TrySetResult(context);
            if (state.BlockAfterEffect)
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }

            return JsonSerializer.SerializeToElement(new { receipt });
        }
    }
}
