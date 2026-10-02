using System.Net;
using System.Text.Json;
using System.Threading.Channels;

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
public sealed class TaskForgeWorkerCancellationContractTests(SqlServerFixture fixture) : SqlServerTest(fixture)
{
    [Fact]
    public async Task Status_polling_does_not_renew_the_lease_and_persistent_cancellation_stops_the_handler()
    {
        await using WebApplicationFactory<Program> server = CreateServer();
        using HttpClient http = server.CreateClient();
        TaskForgeClient client = Client(http);
        TrafficRecorder traffic = new();
        using HttpClient workerHttp = server.CreateDefaultClient(traffic);
        ExecutionProbe probe = new();
        using IHost host = CreateClientHost(Client(workerHttp), probe);
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(30));
        JobResponse job = await client.SubmitAsync(Request(30), cancellationToken: deadline.Token);
        await host.StartAsync(deadline.Token);
        try
        {
            await probe.Entered.Task.WaitAsync(deadline.Token);
            JobResponse before = await client.GetJobAsync(job.Id, deadline.Token);
            await traffic.Reads.Reader.ReadAsync(deadline.Token);
            await traffic.Reads.Reader.ReadAsync(deadline.Token);
            JobResponse after = await client.GetJobAsync(job.Id, deadline.Token);
            Assert.Equal(JobStatus.Processing, after.Status);
            Assert.Equal(before.LeaseExpiresAtUtc, after.LeaseExpiresAtUtc);
            Assert.Equal(before.OwningWorkerId, after.OwningWorkerId);
            Assert.Equal(before.StartedAtUtc, after.StartedAtUtc);

            JobResponse cancelled = await client.CancelAsync(job.Id, deadline.Token);
            Assert.Equal(JobStatus.Cancelled, cancelled.Status);
            await probe.Cancelled.Task.WaitAsync(deadline.Token);
            await probe.Disposed.Task.WaitAsync(deadline.Token);
            JobAttemptResponse attempt = Assert.Single(await client.GetAttemptsAsync(job.Id, deadline.Token));
            Assert.Equal(JobAttemptOutcome.Cancelled, attempt.Outcome);
            Assert.True(probe.Token.IsCancellationRequested);
            Assert.Equal(0, traffic.ReportCount);
            Assert.Equal(1, probe.Invocations);
        }
        finally
        {
            await host.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Ignored_deadline_holds_the_slot_and_a_late_report_cannot_change_a_newer_success(bool lateFailure)
    {
        await using WebApplicationFactory<Program> server = CreateServer();
        using HttpClient http = server.CreateClient();
        TaskForgeClient client = Client(http);
        TrafficRecorder traffic = new();
        using HttpClient workerHttp = server.CreateDefaultClient(traffic);
        ExecutionProbe probe = new() { IgnoreCancellation = true, FailAfterRelease = lateFailure };
        using IHost host = CreateClientHost(Client(workerHttp), probe);
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(30));
        JobResponse job = await client.SubmitAsync(Request(3), cancellationToken: deadline.Token);
        await host.StartAsync(deadline.Token);
        try
        {
            await probe.Entered.Task.WaitAsync(deadline.Token);
            JobAttemptResponse original = Assert.Single(await client.GetAttemptsAsync(job.Id, deadline.Token));
            await probe.Cancelled.Task.WaitAsync(deadline.Token);
            Assert.False(probe.Disposed.Task.IsCompleted);
            Assert.Equal(1, traffic.WaitCount);
            Assert.Equal(0, traffic.ReportCount);

            ExecutionAssignmentResponse retry = Assert.IsType<ExecutionAssignmentResponse>(await client.WaitAsync(new("recovery-worker", ["sdk-cancellation"], 10), deadline.Token));
            Assert.Equal(job.Id, retry.JobId);
            Assert.Equal(2, retry.AttemptNumber);
            Assert.NotEqual(original.AttemptId, retry.AttemptId);
            await client.CompleteAsync(retry.JobId, retry.AttemptId, new(retry.WorkerId, JsonSerializer.SerializeToElement(99)), deadline.Token);
            IReadOnlyList<JobAttemptResponse> beforeLateReport = await client.GetAttemptsAsync(job.Id, deadline.Token);

            probe.Release.TrySetResult();
            CapturedReport report = await traffic.Reports.Reader.ReadAsync(deadline.Token);
            await probe.Disposed.Task.WaitAsync(deadline.Token);
            Assert.Equal(HttpStatusCode.Conflict, report.Status);
            Assert.Equal($"/api/jobs/{job.Id:D}/attempts/{original.AttemptId:D}/{(lateFailure ? "fail" : "complete")}", report.Path);
            using JsonDocument error = JsonDocument.Parse(report.ResponseBody);
            Assert.Equal("AttemptTimedOut", error.RootElement.GetProperty("code").GetString());
            JobResponse final = await client.GetJobAsync(job.Id, deadline.Token);
            Assert.Equal(JobStatus.Completed, final.Status);
            Assert.Equal(99, final.Result!.Value.GetInt32());
            Assert.Equal(1, final.RetryCount);
            Assert.Null(final.OwningWorkerId);
            Assert.Equal(beforeLateReport, await client.GetAttemptsAsync(job.Id, deadline.Token));
            Assert.Equal([JobAttemptOutcome.TimedOut, JobAttemptOutcome.Succeeded], beforeLateReport.Select(attempt => attempt.Outcome));
            Assert.Equal(1, probe.Invocations);
        }
        finally
        {
            probe.Release.TrySetResult();
            await host.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    public async Task Host_shutdown_leaves_unreported_work_for_server_recovery_and_a_new_client_session()
    {
        await using WebApplicationFactory<Program> server = CreateServer();
        using HttpClient http = server.CreateClient();
        TaskForgeClient client = Client(http);
        TrafficRecorder traffic = new();
        using HttpClient workerHttp = server.CreateDefaultClient(traffic);
        ExecutionProbe original = new();
        using IHost firstHost = CreateClientHost(Client(workerHttp), original);
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(30));
        JobResponse job = await client.SubmitAsync(Request(3), cancellationToken: deadline.Token);
        await firstHost.StartAsync(deadline.Token);
        try
        {
            await original.Entered.Task.WaitAsync(deadline.Token);
            await firstHost.StopAsync(deadline.Token);
            await original.Cancelled.Task.WaitAsync(deadline.Token);
            await original.Disposed.Task.WaitAsync(deadline.Token);
            Assert.Equal(0, traffic.ReportCount);
            await WaitForStatusAsync(client, job.Id, JobStatus.Retrying, deadline.Token);
            JobAttemptResponse timedOut = Assert.Single(await client.GetAttemptsAsync(job.Id, deadline.Token));
            Assert.Equal(JobAttemptOutcome.TimedOut, timedOut.Outcome);

            ExecutionProbe restarted = new();
            restarted.Release.TrySetResult();
            using IHost secondHost = CreateClientHost(client, restarted);
            await secondHost.StartAsync(deadline.Token);
            try
            {
                JobResponse completed = await WaitForStatusAsync(client, job.Id, JobStatus.Completed, deadline.Token);
                Assert.Equal(42, completed.Result!.Value.GetInt32());
                Assert.Equal(1, completed.RetryCount);
                IReadOnlyList<JobAttemptResponse> attempts = await client.GetAttemptsAsync(job.Id, deadline.Token);
                Assert.Equal(2, attempts.Count);
                Assert.Equal(timedOut, attempts[0]);
                Assert.Equal(JobAttemptOutcome.Succeeded, attempts[1].Outcome);
                Assert.NotEqual(attempts[0].WorkerId, attempts[1].WorkerId);
                Assert.NotEqual(attempts[0].AttemptId, attempts[1].AttemptId);
                Assert.Equal(1, restarted.Invocations);
            }
            finally
            {
                await secondHost.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
            }
        }
        finally
        {
            await firstHost.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    private WebApplicationFactory<Program> CreateServer() => new WebApplicationFactory<Program>().WithWebHostBuilder(builder => builder
        .UseSetting("ConnectionStrings:TaskForge", ConnectionString).UseSetting("Worker:RetryDelaySeconds", "1").UseSetting("Worker:PollIntervalMilliseconds", "50"));

    private static TaskForgeClient Client(HttpClient http) => new(http, new() { BaseUrl = http.BaseAddress!, ApplicationId = "a-project" });

    private static SubmitJobRequest Request(int timeout) => new("sdk-cancellation", JsonSerializer.SerializeToElement(new { value = 21 }), MaxRetries: 1, TimeoutSeconds: timeout);

    private static IHost CreateClientHost(TaskForgeClient client, ExecutionProbe probe) => new HostBuilder().ConfigureServices(services =>
    {
        services.AddSingleton(client);
        services.AddSingleton(probe);
        services.AddTaskForgeHandlers(handlers => handlers.Register<WorkPayload, WaitingHandler>("sdk-cancellation"));
        services.AddTaskForgeWorker(new() { WaitSeconds = 1, StatusPollInterval = TimeSpan.FromMilliseconds(50), ShutdownTimeout = TimeSpan.FromSeconds(2) });
    }).Build();

    private static async Task<JobResponse> WaitForStatusAsync(TaskForgeClient client, Guid jobId, JobStatus status, CancellationToken cancellationToken)
    {
        while (true)
        {
            JobResponse job = await client.GetJobAsync(jobId, cancellationToken);
            if (job.Status == status)
            {
                return job;
            }

            await Task.Delay(50, cancellationToken);
        }
    }

    private sealed record CapturedReport(string Path, HttpStatusCode Status, string ResponseBody);

    private sealed class TrafficRecorder : DelegatingHandler
    {
        public Channel<bool> Reads { get; } = Channel.CreateUnbounded<bool>();
        public Channel<CapturedReport> Reports { get; } = Channel.CreateUnbounded<CapturedReport>();
        public int WaitCount;
        public int ReportCount;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string path = request.RequestUri!.AbsolutePath;
            if (path == "/api/executions/wait")
            {
                Interlocked.Increment(ref WaitCount);
            }

            HttpResponseMessage response = await base.SendAsync(request, cancellationToken);
            if (request.Method == HttpMethod.Get)
            {
                Reads.Writer.TryWrite(true);
            }
            else if (path.EndsWith("/complete") || path.EndsWith("/fail"))
            {
                Interlocked.Increment(ref ReportCount);
                Reports.Writer.TryWrite(new(path, response.StatusCode, await response.Content.ReadAsStringAsync(cancellationToken)));
            }

            return response;
        }
    }

    public sealed record WorkPayload(int Value);

    public sealed class ExecutionProbe
    {
        public bool IgnoreCancellation { get; init; }
        public bool FailAfterRelease { get; init; }
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Disposed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public CancellationToken Token { get; set; }
        public int Invocations;
    }

    public sealed class WaitingHandler(ExecutionProbe probe) : IJobHandler<WorkPayload>, IAsyncDisposable
    {
        public async Task<JsonElement?> HandleAsync(WorkPayload payload, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref probe.Invocations);
            probe.Token = cancellationToken;
            using CancellationTokenRegistration registration = cancellationToken.Register(() => probe.Cancelled.TrySetResult());
            probe.Entered.TrySetResult();
            try
            {
                if (probe.IgnoreCancellation)
                {
                    await probe.Release.Task;
                }
                else
                {
                    await probe.Release.Task.WaitAsync(cancellationToken);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                probe.Cancelled.TrySetResult();
                throw;
            }

            if (probe.FailAfterRelease)
            {
                throw new InvalidOperationException("Late client failure.");
            }

            return JsonSerializer.SerializeToElement(payload.Value * 2);
        }

        public async ValueTask DisposeAsync()
        {
            await Task.Yield();
            probe.Disposed.TrySetResult();
        }
    }
}
