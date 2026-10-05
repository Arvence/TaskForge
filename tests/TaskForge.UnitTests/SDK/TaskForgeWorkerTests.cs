using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

using TaskForge.SDK;
using TaskForge.SDK.Execution;
using TaskForge.SDK.Handlers;
using TaskForge.SDK.Jobs;

namespace TaskForge.UnitTests.SDK;

public sealed partial class TaskForgeWorkerTests
{
    [Fact]
    public async Task No_work_is_followed_by_new_waits_and_repeated_assignments_keep_their_exact_identity()
    {
        await using WorkerFixture fixture = new();
        Assignment first = Assignment.Create();
        Assignment second = Assignment.Create();
        fixture.Server.Assignments.Writer.TryWrite(null);
        fixture.Server.Assignments.Writer.TryWrite(null);
        fixture.Server.Assignments.Writer.TryWrite(first);
        fixture.Server.Assignments.Writer.TryWrite(second);

        await fixture.Worker.StartAsync(default);
        Report firstReport = await ReadAsync(fixture.Server.Reports.Reader);
        Report secondReport = await ReadAsync(fixture.Server.Reports.Reader);
        for (int i = 0; i < 5; i++)
        {
            JsonElement wait = await ReadAsync(fixture.Server.Waits.Reader);
            Assert.Equal("a-project", wait.GetProperty("applicationId").GetString());
            Assert.Equal(["double"], wait.GetProperty("supportedTypes").EnumerateArray().Select(value => value.GetString()));
            Assert.Equal(20, wait.GetProperty("waitSeconds").GetInt32());
            Assert.Equal(firstReport.Body.GetProperty("workerId").GetString(), wait.GetProperty("workerId").GetString());
        }

        AssertReport(first, firstReport, "complete");
        AssertReport(second, secondReport, "complete");
        Assert.Equal(42, firstReport.Body.GetProperty("result").GetInt32());
        Assert.Equal(2, fixture.State.Created);
        Assert.Equal(2, fixture.State.Disposed);
        Assert.Equal(1, fixture.State.MaximumActive);
        Assert.Equal(2, fixture.State.ScopeIds.Distinct().Count());
    }

    [Fact]
    public async Task Default_slot_does_not_wait_for_more_work_while_its_handler_is_running()
    {
        await using WorkerFixture fixture = new();
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.State.Handle = async (_, token) =>
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(token);
            return null;
        };
        fixture.Server.Assignments.Writer.TryWrite(Assignment.Create());
        await fixture.Worker.StartAsync(default);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(1, fixture.Server.WaitCount);
        Assert.Equal(1, fixture.State.Active);
        Assert.Equal(0, fixture.State.Disposed);
        Assert.Equal(0, fixture.Server.ReportCount);
        release.TrySetResult();

        Report report = await ReadAsync(fixture.Server.Reports.Reader);
        Assert.Equal(JsonValueKind.Null, report.Body.GetProperty("result").ValueKind);
        Assert.Equal(1, fixture.State.Disposed);
    }

    [Fact]
    public async Task Failed_attempt_is_not_invoked_again_until_the_server_assigns_a_new_attempt()
    {
        await using WorkerFixture fixture = new();
        Assignment first = Assignment.Create();
        fixture.State.Handle = (_, _) => throw new InvalidOperationException("Temporary dependency failure.");
        fixture.Server.Assignments.Writer.TryWrite(first);
        await fixture.Worker.StartAsync(default);
        Report failure = await ReadAsync(fixture.Server.Reports.Reader);
        await ReadAsync(fixture.Server.Waits.Reader);
        await ReadAsync(fixture.Server.Waits.Reader);

        AssertReport(first, failure, "fail");
        Assert.Equal(1, fixture.State.Created);
        Assert.Equal(1, fixture.Server.ReportCount);
        Assert.Equal("InvalidOperationException", failure.Body.GetProperty("errorCode").GetString());
        fixture.State.Handle = (_, _) => Task.FromResult<JsonElement?>(JsonSerializer.SerializeToElement(42));
        Assignment retry = first with { AttemptId = Guid.NewGuid(), AttemptNumber = 2 };
        fixture.Server.Assignments.Writer.TryWrite(retry);
        Report success = await ReadAsync(fixture.Server.Reports.Reader);

        AssertReport(retry, success, "complete");
        Assert.Equal(2, fixture.State.Created);
        Assert.Equal(2, fixture.State.Disposed);
        Assert.Equal(failure.Body.GetProperty("workerId").GetString(), success.Body.GetProperty("workerId").GetString());
    }

    [Theory]
    [InlineData("payload", "InvalidPayload", 0)]
    [InlineData("missing", "UnsupportedJobType", 0)]
    [InlineData("handler", "InvalidOperationException", 1)]
    [InlineData("permanent", "NonRetryableJobException", 1)]
    [InlineData("handler-json", "JsonException", 1)]
    [InlineData("handler-io", "IOException", 1)]
    [InlineData("handler-cancel", "OperationCanceledException", 1)]
    [InlineData("undefined", "ResultSerializationFailed", 1)]
    [InlineData("disposed", "ResultSerializationFailed", 1)]
    [InlineData("oversized", "ResultSerializationFailed", 1)]
    [InlineData("serialization", "ResultSerializationFailed", 1)]
    public async Task Local_failures_are_reported_once_with_the_expected_code(string scenario, string code, int expectedHandlers)
    {
        await using WorkerFixture fixture = new();
        Assignment assignment = Assignment.Create();
        if (scenario == "payload")
        {
            assignment = assignment with { Payload = JsonSerializer.SerializeToElement(new { number = "not an integer" }) };
        }
        else if (scenario == "missing")
        {
            assignment = assignment with { Type = "unregistered" };
        }

        fixture.State.Handle = (_, _) => scenario switch
        {
            "handler" => throw new InvalidOperationException("Handler failed."),
            "permanent" => throw new NonRetryableJobException("Business input cannot be processed."),
            "handler-json" => throw new JsonException("Handler JSON operation failed."),
            "handler-io" => throw new IOException("Handler file operation failed."),
            "handler-cancel" => throw new OperationCanceledException("Local dependency cancelled."),
            "undefined" => Task.FromResult<JsonElement?>(default(JsonElement)),
            "disposed" => Task.FromResult<JsonElement?>(DisposedResult()),
            "oversized" => Task.FromResult<JsonElement?>(JsonSerializer.SerializeToElement(new string('x', 4000))),
            "serialization" => throw new JobResultSerializationException("Business result cannot be serialized."),
            _ => Task.FromResult<JsonElement?>(null)
        };
        fixture.Server.Assignments.Writer.TryWrite(assignment);
        await fixture.Worker.StartAsync(default);
        Report report = await ReadAsync(fixture.Server.Reports.Reader);

        AssertReport(assignment, report, "fail");
        Assert.Equal(code, report.Body.GetProperty("errorCode").GetString());
        Assert.False(string.IsNullOrWhiteSpace(report.Body.GetProperty("errorMessage").GetString()));
        Assert.Equal(expectedHandlers, fixture.State.Created);
        Assert.Equal(expectedHandlers, fixture.State.Disposed);
        Assert.False(report.Body.TryGetProperty("result", out _));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(5000)]
    public async Task Failure_messages_are_nonempty_and_fit_the_server_contract(int length)
    {
        await using WorkerFixture fixture = new();
        fixture.State.Handle = (_, _) => throw new InvalidOperationException(new string('x', length));
        fixture.Server.Assignments.Writer.TryWrite(Assignment.Create());
        await fixture.Worker.StartAsync(default);
        Report report = await ReadAsync(fixture.Server.Reports.Reader);
        Assert.InRange(report.Body.GetProperty("errorMessage").GetString()!.Length, 1, 4000);
    }

    [Theory]
    [InlineData("application")]
    [InlineData("worker")]
    [InlineData("job")]
    [InlineData("attempt")]
    public async Task Invalid_assignment_identity_stops_the_worker_without_invocation_or_reporting(string mismatch)
    {
        await using WorkerFixture fixture = new();
        Assignment assignment = Assignment.Create();
        fixture.Server.Assignments.Writer.TryWrite(mismatch switch
        {
            "application" => assignment with { ApplicationId = "b-project" },
            "worker" => assignment with { WorkerId = "someone-else" },
            "job" => assignment with { JobId = Guid.Empty },
            _ => assignment with { AttemptId = Guid.Empty }
        });
        await fixture.Worker.StartAsync(default);

        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Worker.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(0, fixture.State.Created);
        Assert.Equal(0, fixture.Server.ReportCount);
        Assert.Equal(1, fixture.Server.WaitCount);
    }

    [Fact]
    public async Task Multiple_slots_have_distinct_worker_ids_and_one_active_execution_per_slot()
    {
        await using WorkerFixture fixture = new(new() { SlotCount = 2 });
        TaskCompletionSource bothEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.State.Handle = async (_, token) =>
        {
            if (fixture.State.Active == 2)
            {
                bothEntered.TrySetResult();
            }

            await release.Task.WaitAsync(token);
            return null;
        };
        fixture.Server.Assignments.Writer.TryWrite(Assignment.Create());
        fixture.Server.Assignments.Writer.TryWrite(Assignment.Create());
        await fixture.Worker.StartAsync(default);
        await bothEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        JsonElement firstWait = await ReadAsync(fixture.Server.Waits.Reader);
        JsonElement secondWait = await ReadAsync(fixture.Server.Waits.Reader);

        Assert.Equal(2, fixture.Server.WaitCount);
        Assert.Equal(2, fixture.State.Active);
        Assert.NotEqual(firstWait.GetProperty("workerId").GetString(), secondWait.GetProperty("workerId").GetString());
        release.TrySetResult();
        Report firstReport = await ReadAsync(fixture.Server.Reports.Reader);
        Report secondReport = await ReadAsync(fixture.Server.Reports.Reader);
        Assert.NotEqual(firstReport.Body.GetProperty("workerId").GetString(), secondReport.Body.GetProperty("workerId").GetString());
        Assert.Equal(2, fixture.State.Disposed);
    }

    [Fact]
    public async Task A_fatal_assignment_error_cancels_the_other_slots()
    {
        await using WorkerFixture fixture = new(new() { SlotCount = 2 });
        fixture.Server.Assignments.Writer.TryWrite(Assignment.Create() with { ApplicationId = "b-project" });
        await fixture.Worker.StartAsync(default);
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Worker.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(0, fixture.State.Created);
    }

    [Fact]
    public async Task Separate_worker_sessions_never_reuse_the_slot_identity()
    {
        await using WorkerFixture first = new();
        await using WorkerFixture second = new();
        await first.Worker.StartAsync(default);
        await second.Worker.StartAsync(default);
        string firstId = (await ReadAsync(first.Server.Waits.Reader)).GetProperty("workerId").GetString()!;
        string secondId = (await ReadAsync(second.Server.Waits.Reader)).GetProperty("workerId").GetString()!;
        Assert.NotEqual(firstId, secondId);
        Assert.InRange(firstId.Length, 1, 200);
        Assert.EndsWith("-1", firstId);
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.Conflict)]
    public async Task Rejected_completion_does_not_trigger_a_failure_report_or_reexecution(HttpStatusCode status)
    {
        await using WorkerFixture fixture = new();
        fixture.Server.ReportStatuses.Enqueue(status);
        Assignment first = Assignment.Create();
        Assignment second = Assignment.Create();
        fixture.Server.Assignments.Writer.TryWrite(first);
        fixture.Server.Assignments.Writer.TryWrite(second);
        await fixture.Worker.StartAsync(default);
        AssertReport(first, await ReadAsync(fixture.Server.Reports.Reader), "complete");
        AssertReport(second, await ReadAsync(fixture.Server.Reports.Reader), "complete");
        Assert.Equal(2, fixture.State.Created);
        Assert.Equal(2, fixture.Server.ReportCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Transport_failure_during_wait_is_followed_only_by_another_wait(bool interruptedStream)
    {
        await using WorkerFixture fixture = new();
        if (interruptedStream)
        {
            fixture.Server.WaitResponses.Enqueue(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new InterruptedResponseStream()) });
        }
        else
        {
            fixture.Server.WaitStatuses.Enqueue(HttpStatusCode.ServiceUnavailable);
        }
        fixture.Server.Assignments.Writer.TryWrite(Assignment.Create());
        await fixture.Worker.StartAsync(default);
        await ReadAsync(fixture.Server.Reports.Reader);
        await ReadAsync(fixture.Server.Waits.Reader);
        await ReadAsync(fixture.Server.Waits.Reader);
        Assert.Equal(1, fixture.State.Created);
        Assert.Equal(1, fixture.Server.ReportCount);
    }

    [Fact]
    public async Task Stopping_cancels_the_active_handler_disposes_its_scope_and_does_not_report_business_failure()
    {
        await using WorkerFixture fixture = new();
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.State.Handle = async (_, token) =>
        {
            entered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return null;
        };
        fixture.Server.Assignments.Writer.TryWrite(Assignment.Create());
        await fixture.Worker.StartAsync(default);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await fixture.Worker.StopAsync(default).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, fixture.Server.ReportCount);
        Assert.Equal(1, fixture.State.Disposed);
        Assert.Equal(0, fixture.State.Active);
    }

    [Fact]
    public async Task Result_limit_uses_compact_server_serialization_instead_of_raw_payload_length()
    {
        await using WorkerFixture fixture = new();
        using JsonDocument result = JsonDocument.Parse("[" + new string(' ', 5000) + "42]");
        fixture.State.Handle = (_, _) => Task.FromResult<JsonElement?>(result.RootElement);
        fixture.Server.Assignments.Writer.TryWrite(Assignment.Create());
        await fixture.Worker.StartAsync(default);
        Report report = await ReadAsync(fixture.Server.Reports.Reader);
        Assert.EndsWith("/complete", report.Path);
        Assert.Equal(42, report.Body.GetProperty("result")[0].GetInt32());
    }

    [Fact]
    public void Hosting_is_opt_in_and_duplicate_registration_is_rejected()
    {
        ServiceCollection services = new();
        services.AddTaskForgeHandlers(registry => registry.Register<NumberPayload, ProbeHandler>("double"));
        Assert.DoesNotContain(services, descriptor => descriptor.ServiceType == typeof(IHostedService));
        services.AddTaskForgeWorker();
        Assert.Single(services, descriptor => descriptor.ServiceType == typeof(IHostedService));
        Assert.Throws<InvalidOperationException>(() => services.AddTaskForgeWorker());
    }

    [Theory]
    [InlineData(0, 20, 1)]
    [InlineData(1, -1, 1)]
    [InlineData(1, 31, 1)]
    [InlineData(1, 20, 0)]
    public void Invalid_options_are_rejected_before_host_start(int slots, int wait, int delay)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ServiceCollection().AddTaskForgeWorker(new()
        {
            SlotCount = slots,
            WaitSeconds = wait,
            NoWorkDelay = TimeSpan.FromMilliseconds(delay)
        }));
    }

    [Fact]
    public async Task An_empty_registry_cannot_start_a_worker()
    {
        ServiceCollection services = new();
        services.AddTaskForgeHandlers(_ => { });
        await using ServiceProvider provider = services.BuildServiceProvider();
        using HttpClient http = new();
        TaskForgeClient client = new(http, new() { ApplicationId = "a-project" });
        Assert.Throws<ArgumentException>(() => new TaskForgeWorker(client, provider.GetRequiredService<JobHandlerRegistry>(), new()));
    }

    private static async Task<T> ReadAsync<T>(ChannelReader<T> reader) => await reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));

    private static JsonElement DisposedResult()
    {
        using JsonDocument document = JsonDocument.Parse("42");
        return document.RootElement;
    }

    private static void AssertReport(Assignment assignment, Report report, string operation)
    {
        Assert.Equal($"/api/jobs/{assignment.JobId:D}/attempts/{assignment.AttemptId:D}/{operation}", report.Path);
        Assert.Equal("a-project", report.Body.GetProperty("applicationId").GetString());
        Assert.StartsWith("taskforge-", report.Body.GetProperty("workerId").GetString());
        Assert.False(report.Body.TryGetProperty("retryCount", out _));
        Assert.False(report.Body.TryGetProperty("status", out _));
    }

    private sealed class WorkerFixture : IAsyncDisposable
    {
        private readonly ServiceProvider _provider;
        private readonly HttpClient _http;

        public WorkerFixture(TaskForgeWorkerOptions? options = null, TimeProvider? timeProvider = null, IHostApplicationLifetime? lifetime = null)
        {
            Server.Clock = timeProvider ?? TimeProvider.System;
            _http = new HttpClient(Server);
            ServiceCollection services = new();
            if (timeProvider is not null)
            {
                services.AddSingleton(timeProvider);
            }

            if (lifetime is not null)
            {
                services.AddSingleton(lifetime);
            }

            services.AddSingleton(State);
            services.AddSingleton(new TaskForgeClient(_http, new() { ApplicationId = " A-Project " }));
            services.AddTaskForgeHandlers(registry => registry.Register<NumberPayload, ProbeHandler>("double"));
            services.AddTaskForgeWorker(options ?? new() { NoWorkDelay = TimeSpan.FromMilliseconds(1), TransportErrorDelay = TimeSpan.FromMilliseconds(1) });
            _provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
            Worker = Assert.IsType<TaskForgeWorker>(Assert.Single(_provider.GetServices<IHostedService>()));
        }

        public ScriptedServer Server { get; } = new();
        public ProbeState State { get; } = new();
        public TaskForgeWorker Worker { get; }

        public async ValueTask DisposeAsync()
        {
            await Worker.StopAsync(default).WaitAsync(TimeSpan.FromSeconds(5));
            await _provider.DisposeAsync();
            _http.Dispose();
        }
    }

    private sealed record Assignment(Guid JobId, Guid AttemptId, string Type, JsonElement Payload, string? ApplicationId = null, string? WorkerId = null, int AttemptNumber = 1, DateTimeOffset? StartedAtUtc = null, DateTimeOffset? DeadlineAtUtc = null, DateTimeOffset? LeaseExpiresAtUtc = null, int TimeoutSeconds = 30)
    {
        public static Assignment Create() => new(Guid.NewGuid(), Guid.NewGuid(), "DOUBLE", JsonSerializer.SerializeToElement(new { number = 21 }));
    }

    private sealed record Report(string Path, JsonElement Body, string RawBody);

    private sealed class InterruptedResponseStream : MemoryStream
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            ValueTask.FromException<int>(new IOException("Response stream disconnected."));
    }

    private sealed class ScriptedServer : HttpMessageHandler
    {
        public Channel<Assignment?> Assignments { get; } = Channel.CreateUnbounded<Assignment?>();
        public Channel<JsonElement> Waits { get; } = Channel.CreateUnbounded<JsonElement>();
        public Channel<Report> Reports { get; } = Channel.CreateUnbounded<Report>();
        public Channel<ExecutionAssignmentResponse> StatusReads { get; } = Channel.CreateUnbounded<ExecutionAssignmentResponse>();
        public Func<ExecutionAssignmentResponse, CancellationToken, Task<HttpResponseMessage>>? ReadJob { get; set; }
        public Func<Report, CancellationToken, Task<HttpResponseMessage>>? SendReport { get; set; }
        public TimeProvider Clock { get; set; } = TimeProvider.System;
        private readonly ConcurrentDictionary<Guid, ExecutionAssignmentResponse> _running = new();
        public ConcurrentQueue<HttpStatusCode> WaitStatuses { get; } = new();
        public ConcurrentQueue<HttpResponseMessage> WaitResponses { get; } = new();
        public ConcurrentQueue<HttpStatusCode> ReportStatuses { get; } = new();
        public int WaitCount;
        public int ReportCount;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Get)
            {
                Assert.Null(request.Content);
                Guid jobId = Guid.Parse(request.RequestUri!.Segments.Last());
                Assert.Equal($"/api/jobs/{jobId:D}", request.RequestUri.AbsolutePath);
                ExecutionAssignmentResponse assignment = _running[jobId];
                StatusReads.Writer.TryWrite(assignment);
                return ReadJob is null ? Status(assignment) : await ReadJob(assignment, cancellationToken);
            }

            Assert.Equal(HttpMethod.Post, request.Method);
            string rawBody = await request.Content!.ReadAsStringAsync(cancellationToken);
            using JsonDocument document = JsonDocument.Parse(rawBody);
            JsonElement body = document.RootElement.Clone();
            if (request.RequestUri!.AbsolutePath == "/api/executions/wait")
            {
                Interlocked.Increment(ref WaitCount);
                Waits.Writer.TryWrite(body);
                if (WaitResponses.TryDequeue(out HttpResponseMessage? waitResponse))
                {
                    return waitResponse;
                }

                if (WaitStatuses.TryDequeue(out HttpStatusCode status))
                {
                    return Json("{}", status);
                }

                Assignment? next = await Assignments.Reader.ReadAsync(cancellationToken);
                if (next is null)
                {
                    return new(HttpStatusCode.NoContent);
                }

                DateTimeOffset now = next.StartedAtUtc ?? Clock.GetUtcNow();
                ExecutionAssignmentResponse assignment = new(next.JobId, next.ApplicationId ?? "a-project", next.AttemptId, next.AttemptNumber,
                    next.WorkerId ?? body.GetProperty("workerId").GetString()!, next.Type, next.Payload, next.TimeoutSeconds, now,
                    next.DeadlineAtUtc ?? now.AddSeconds(next.TimeoutSeconds), next.LeaseExpiresAtUtc ?? now.AddSeconds(next.TimeoutSeconds + 10));
                _running[assignment.JobId] = assignment;
                return Json(JsonSerializer.Serialize(assignment, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
            }

            Interlocked.Increment(ref ReportCount);
            Report report = new(request.RequestUri.AbsolutePath, body, rawBody);
            Reports.Writer.TryWrite(report);
            if (SendReport is not null)
            {
                return await SendReport(report, cancellationToken);
            }

            return Json("{\"retryCount\":999}", ReportStatuses.TryDequeue(out HttpStatusCode reportStatus) ? reportStatus : HttpStatusCode.OK);
        }

        private static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) => new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

        public static HttpResponseMessage Status(ExecutionAssignmentResponse assignment, bool cancellationRequested = false, JobStatus status = JobStatus.Processing)
        {
            JobResponse job = new(assignment.JobId, assignment.ApplicationId, assignment.Type, assignment.Payload, null, null, JobPriority.Normal,
                status, 3, 0, assignment.TimeoutSeconds, cancellationRequested, assignment.StartedAtUtc, assignment.StartedAtUtc, assignment.StartedAtUtc,
                assignment.StartedAtUtc, null, null, assignment.WorkerId, assignment.LeaseExpiresAtUtc, null);
            JsonSerializerOptions options = new(JsonSerializerDefaults.Web);
            options.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());
            return Json(JsonSerializer.Serialize(job, options));
        }
    }

    public sealed record NumberPayload(int Number);

    public sealed class ProbeState
    {
        public Func<NumberPayload, CancellationToken, Task<JsonElement?>> Handle { get; set; } = (payload, _) => Task.FromResult<JsonElement?>(JsonSerializer.SerializeToElement(payload.Number * 2));
        public ConcurrentQueue<Guid> ScopeIds { get; } = new();
        public int Created;
        public int Disposed;
        public int Active;
        public int MaximumActive;
    }

    public sealed class ProbeHandler : IJobHandler<NumberPayload>, IAsyncDisposable
    {
        private readonly ProbeState _state;

        public ProbeHandler(ProbeState state)
        {
            _state = state;
            Interlocked.Increment(ref state.Created);
            state.ScopeIds.Enqueue(Guid.NewGuid());
        }

        public async Task<JsonElement?> HandleAsync(NumberPayload payload, CancellationToken cancellationToken = default)
        {
            int active = Interlocked.Increment(ref _state.Active);
            Interlocked.Exchange(ref _state.MaximumActive, Math.Max(active, _state.MaximumActive));
            try
            {
                return await _state.Handle(payload, cancellationToken);
            }
            finally
            {
                Interlocked.Decrement(ref _state.Active);
            }
        }

        public async ValueTask DisposeAsync()
        {
            await Task.Yield();
            Interlocked.Increment(ref _state.Disposed);
        }
    }
}
