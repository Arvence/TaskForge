using System.Net;
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
public sealed class TaskForgeWorkerDeliveryContractTests(SqlServerFixture fixture) : SqlServerTest(fixture)
{
    [Theory]
    [InlineData(false, true)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    [InlineData(true, false)]
    public async Task Uncertain_delivery_repeats_the_outcome_without_reexecuting_or_consuming_extra_job_retries(bool failure, bool dropAcknowledgement)
    {
        await using WebApplicationFactory<Program> server = new WebApplicationFactory<Program>().WithWebHostBuilder(builder => builder
            .UseSetting("ConnectionStrings:TaskForge", ConnectionString).UseSetting("Worker:RetryDelaySeconds", "30"));
        using HttpClient http = server.CreateClient();
        TaskForgeClient client = Client(http);
        DeliveryProbe traffic = new(client, dropAcknowledgement);
        using HttpClient workerHttp = server.CreateDefaultClient(traffic);
        HandlerProbe handler = new() { Fail = failure };
        using IHost host = new HostBuilder().ConfigureServices(services =>
        {
            services.AddSingleton(Client(workerHttp));
            services.AddSingleton(handler);
            services.AddTaskForgeHandlers(registry => registry.Register<WorkPayload, WorkHandler>("sdk-delivery"));
            services.AddTaskForgeWorker(new() { WaitSeconds = 1, TransportErrorDelay = TimeSpan.FromMilliseconds(50) });
        }).Build();
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(30));
        JobResponse submitted = await client.SubmitAsync(new("sdk-delivery", JsonSerializer.SerializeToElement(new { value = 21 }), MaxRetries: 1, TimeoutSeconds: 30), cancellationToken: deadline.Token);
        await host.StartAsync(deadline.Token);
        try
        {
            await traffic.NextWait.Task.WaitAsync(deadline.Token);
            JobResponse final = await client.GetJobAsync(submitted.Id, deadline.Token);
            IReadOnlyList<JobAttemptResponse> history = await client.GetAttemptsAsync(submitted.Id, deadline.Token);
            JobAttemptResponse attempt = Assert.Single(history);
            Assert.Equal(failure ? JobStatus.Retrying : JobStatus.Completed, final.Status);
            Assert.Equal(failure ? 1 : 0, final.RetryCount);
            Assert.Equal(failure ? JobAttemptOutcome.Failed : JobAttemptOutcome.Succeeded, attempt.Outcome);
            Assert.Equal(1, attempt.AttemptNumber);
            Assert.Equal(1, handler.Invocations);
            Assert.Equal(1, handler.Disposed);
            Assert.Equal(2, traffic.Requests.Count);
            Assert.Equal(traffic.Requests[0], traffic.Requests[1]);
            Assert.Equal($"/api/jobs/{submitted.Id:D}/attempts/{attempt.AttemptId:D}/{(failure ? "fail" : "complete")}", traffic.Requests[0].Path);
            using JsonDocument body = JsonDocument.Parse(traffic.Requests[0].Body);
            Assert.Equal(attempt.WorkerId, body.RootElement.GetProperty("workerId").GetString());
            Assert.Equal("a-project", body.RootElement.GetProperty("applicationId").GetString());

            Assert.Equal(dropAcknowledgement ? 2 : 1, traffic.Accepted.Count);
            Assert.All(traffic.Accepted, accepted =>
            {
                Assert.Equal(HttpStatusCode.OK, accepted.Status);
                Assert.Equal(history, accepted.History);
            });
            if (dropAcknowledgement)
            {
                Assert.Equal(traffic.Accepted[0].Body, traffic.Accepted[1].Body);
            }

            if (failure)
            {
                Assert.Equal("invalidoperationexception", attempt.ErrorCode);
                Assert.Equal("Original business failure.", attempt.ErrorMessage);
                Assert.NotNull(final.NextRetryAtUtc);
                using JsonDocument accepted = JsonDocument.Parse(traffic.Accepted[0].Body);
                Assert.Equal(final.NextRetryAtUtc, accepted.RootElement.GetProperty("nextRetryAtUtc").GetDateTimeOffset());
            }
            else
            {
                Assert.Equal(42, final.Result!.Value.GetInt32());
            }
        }
        finally
        {
            await host.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    private static TaskForgeClient Client(HttpClient http) => new(http, new() { BaseUrl = http.BaseAddress!, ApplicationId = "a-project" });

    private sealed record ReportRequest(string Path, string Body);
    private sealed record AcceptedReport(HttpStatusCode Status, string Body, IReadOnlyList<JobAttemptResponse> History);

    private sealed class DeliveryProbe(TaskForgeClient observer, bool dropAcknowledgement) : DelegatingHandler
    {
        public List<ReportRequest> Requests { get; } = [];
        public List<AcceptedReport> Accepted { get; } = [];
        public TaskCompletionSource NextWait { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string path = request.RequestUri!.AbsolutePath;
            if (path == "/api/executions/wait" && Requests.Count >= 2)
            {
                NextWait.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }

            if (!path.EndsWith("/complete", StringComparison.Ordinal) && !path.EndsWith("/fail", StringComparison.Ordinal))
            {
                return await base.SendAsync(request, cancellationToken);
            }

            Requests.Add(new(path, await request.Content!.ReadAsStringAsync(cancellationToken)));
            if (Requests.Count == 1 && !dropAcknowledgement)
            {
                throw new HttpRequestException("Temporary connection failure before server acceptance.");
            }

            HttpResponseMessage response = await base.SendAsync(request, cancellationToken);
            string body = await response.Content.ReadAsStringAsync(cancellationToken);
            Guid jobId = Guid.Parse(request.RequestUri.Segments[3].TrimEnd('/'));
            Accepted.Add(new(response.StatusCode, body, await observer.GetAttemptsAsync(jobId, cancellationToken)));
            if (Requests.Count == 1)
            {
                response.Dispose();
                throw new HttpRequestException("Server accepted the report, but its acknowledgement was lost.");
            }

            return response;
        }
    }

    public sealed record WorkPayload(int Value);

    public sealed class HandlerProbe
    {
        public bool Fail { get; init; }
        public int Invocations;
        public int Disposed;
    }

    public sealed class WorkHandler(HandlerProbe probe) : IJobHandler<WorkPayload>, IDisposable
    {
        public Task<JsonElement?> HandleAsync(WorkPayload payload, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref probe.Invocations);
            if (probe.Fail)
            {
                throw new InvalidOperationException("Original business failure.");
            }

            return Task.FromResult<JsonElement?>(JsonSerializer.SerializeToElement(payload.Value * 2));
        }

        public void Dispose() => Interlocked.Increment(ref probe.Disposed);
    }
}
