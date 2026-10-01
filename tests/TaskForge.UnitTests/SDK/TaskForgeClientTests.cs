using System.Net;
using System.Text;
using System.Text.Json;

using TaskForge.SDK;
using TaskForge.SDK.Jobs;

namespace TaskForge.UnitTests.SDK;

public sealed class TaskForgeClientTests
{
    private static readonly Guid JobId = Guid.Parse("a1111111-1111-1111-1111-111111111111");
    private static readonly Guid AttemptId = Guid.Parse("b2222222-2222-2222-2222-222222222222");

    [Theory]
    [InlineData(HttpStatusCode.Created)]
    [InlineData(HttpStatusCode.OK)]
    public async Task Submit_uses_configured_scope_string_priority_and_request_local_idempotency(HttpStatusCode status)
    {
        int calls = 0;
        using HttpClient http = new(new StubHandler(async (request, token) =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("http://localhost:8275/prefix/api/jobs", request.RequestUri!.AbsoluteUri);
            Assert.Equal("application/json", request.Content!.Headers.ContentType!.MediaType);
            Assert.Equal("utf-8", request.Content.Headers.ContentType.CharSet);
            if (calls++ == 0)
            {
                Assert.Equal("submission-01", Assert.Single(request.Headers.GetValues("Idempotency-Key")));
            }
            else
            {
                Assert.False(request.Headers.Contains("Idempotency-Key"));
            }

            using JsonDocument body = JsonDocument.Parse(await request.Content.ReadAsStringAsync(token));
            JsonElement root = body.RootElement;
            Assert.Equal(6, root.EnumerateObject().Count());
            Assert.Equal("a-project", root.GetProperty("applicationId").GetString());
            Assert.Equal("calculate", root.GetProperty("type").GetString());
            Assert.Equal("High", root.GetProperty("priority").GetString());
            Assert.Equal(1, root.GetProperty("maxRetries").GetInt32());
            Assert.Equal(45, root.GetProperty("timeoutSeconds").GetInt32());
            Assert.Equal(12.5m, root.GetProperty("payload").GetProperty("amount").GetDecimal());
            Assert.Equal(JsonValueKind.Null, root.GetProperty("payload").GetProperty("optional").ValueKind);
            return Json(JobJson(), status);
        })) { BaseAddress = new Uri("https://unrelated.example/"), Timeout = TimeSpan.FromSeconds(90) };
        TaskForgeClient client = new(http, new() { ApplicationId = " A-PROJECT ", BaseUrl = new Uri("http://localhost:8275/prefix") });
        SubmitJobRequest request = new("calculate", JsonSerializer.SerializeToElement(new { amount = 12.5m, optional = (string?)null }), JobPriority.High, 1, 45);

        JobResponse submitted = await client.SubmitAsync(request, "submission-01");
        await client.SubmitAsync(request);

        Assert.Equal(JobId, submitted.Id);
        Assert.Equal(JobStatus.Completed, submitted.Status);
        Assert.Equal(JobPriority.High, submitted.Priority);
        Assert.Equal(42, submitted.Result!.Value.GetProperty("sum").GetInt32());
        Assert.Equal(90, http.Timeout.TotalSeconds);
        Assert.Equal("https://unrelated.example/", http.BaseAddress!.AbsoluteUri);
        Assert.Empty(http.DefaultRequestHeaders);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(20)]
    [InlineData(30)]
    public async Task Wait_serializes_the_protocol_and_maps_only_204_to_no_assignment(int waitSeconds)
    {
        using HttpClient http = new(new StubHandler(async (request, token) =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("/api/executions/wait", request.RequestUri!.AbsolutePath);
            using JsonDocument body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
            Assert.Equal("a-project", body.RootElement.GetProperty("applicationId").GetString());
            Assert.Equal("Worker-01", body.RootElement.GetProperty("workerId").GetString());
            Assert.Equal(waitSeconds, body.RootElement.GetProperty("waitSeconds").GetInt32());
            Assert.Equal("calculate", body.RootElement.GetProperty("supportedTypes")[0].GetString());
            return new HttpResponseMessage(HttpStatusCode.NoContent);
        }));

        Assert.Null(await Client(http).WaitAsync(new("Worker-01", ["calculate"], waitSeconds)));
    }

    [Fact]
    public async Task Wait_reads_assignment_identity_deadline_and_payload_after_response_disposal()
    {
        using HttpClient http = new(new StubHandler((_, _) => Task.FromResult(Json($$"""
            {"jobId":"{{JobId}}","applicationId":"a-project","attemptId":"{{AttemptId}}","attemptNumber":2,
             "workerId":"Worker-01","type":"calculate","payload":{"items":[1,2]},"timeoutSeconds":30,
             "startedAtUtc":"2026-10-01T12:00:00Z","deadlineAtUtc":"2026-10-01T12:00:30Z","leaseExpiresAtUtc":"2026-10-01T12:01:00Z"}
            """))));

        ExecutionAssignmentResponse assignment = Assert.IsType<ExecutionAssignmentResponse>(await Client(http).WaitAsync(new("Worker-01", ["calculate"])));

        Assert.Equal(JobId, assignment.JobId);
        Assert.Equal(AttemptId, assignment.AttemptId);
        Assert.Equal(2, assignment.AttemptNumber);
        Assert.Equal("Worker-01", assignment.WorkerId);
        Assert.Equal(assignment.StartedAtUtc.AddSeconds(30), assignment.DeadlineAtUtc);
        Assert.Equal(assignment.DeadlineAtUtc.AddSeconds(30), assignment.LeaseExpiresAtUtc);
        Assert.Equal(2, assignment.Payload.GetProperty("items")[1].GetInt32());
    }

    [Theory]
    [InlineData("complete")]
    [InlineData("fail")]
    public async Task Reports_use_attempt_route_and_exact_worker_identity(string operation)
    {
        using HttpClient http = new(new StubHandler(async (request, token) =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal($"/api/jobs/{JobId}/attempts/{AttemptId}/{operation}", request.RequestUri!.AbsolutePath);
            using JsonDocument body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
            Assert.Equal("a-project", body.RootElement.GetProperty("applicationId").GetString());
            Assert.Equal("Exact-Worker", body.RootElement.GetProperty("workerId").GetString());
            if (operation == "complete")
            {
                Assert.Equal(42, body.RootElement.GetProperty("result").GetProperty("sum").GetInt32());
            }
            else
            {
                Assert.Equal("Temporary", body.RootElement.GetProperty("errorCode").GetString());
                Assert.Equal("Dependency unavailable.", body.RootElement.GetProperty("errorMessage").GetString());
                Assert.False(body.RootElement.TryGetProperty("retryCount", out _));
            }

            return Json(JobJson());
        }));
        TaskForgeClient client = Client(http);

        JobResponse result = operation == "complete"
            ? await client.CompleteAsync(JobId, AttemptId, new("Exact-Worker", JsonSerializer.SerializeToElement(new { sum = 42 })))
            : await client.FailAsync(JobId, AttemptId, new("Exact-Worker", "Temporary", "Dependency unavailable."));

        Assert.Equal(JobId, result.Id);
    }

    [Fact]
    public async Task Complete_supports_an_absent_result()
    {
        using HttpClient http = new(new StubHandler(async (request, token) =>
        {
            using JsonDocument body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
            Assert.Equal(JsonValueKind.Null, body.RootElement.GetProperty("result").ValueKind);
            return Json(JobJson());
        }));
        await Client(http).CompleteAsync(JobId, AttemptId, new("worker"));
    }

    [Theory]
    [InlineData("get", "GET", "")]
    [InlineData("history", "GET", "/attempts")]
    [InlineData("cancel", "POST", "/cancel")]
    public async Task Reads_and_cancellation_use_bodyless_job_routes(string operation, string method, string suffix)
    {
        using HttpClient http = new(new StubHandler((request, _) =>
        {
            Assert.Equal(method, request.Method.Method);
            Assert.Equal($"/api/jobs/{JobId}{suffix}", request.RequestUri!.AbsolutePath);
            Assert.Null(request.Content);
            return Task.FromResult(Json(operation == "history" ? "[]" : JobJson()));
        }));
        await InvokeAsync(Client(http), operation, CancellationToken.None);
    }

    [Theory]
    [InlineData("Pending", JobStatus.Pending)]
    [InlineData("Queued", JobStatus.Queued)]
    [InlineData("Processing", JobStatus.Processing)]
    [InlineData("Retrying", JobStatus.Retrying)]
    [InlineData("Completed", JobStatus.Completed)]
    [InlineData("DeadLettered", JobStatus.DeadLettered)]
    [InlineData("Cancelled", JobStatus.Cancelled)]
    public async Task Job_status_uses_server_string_values(string wireStatus, JobStatus expected)
    {
        using HttpClient http = new(new StubHandler((_, _) => Task.FromResult(Json(JobJson(wireStatus)))));
        JobResponse job = await Client(http).GetJobAsync(JobId);
        Assert.Equal(expected, job.Status);
        Assert.Equal("key-01", job.IdempotencyKey);
        Assert.Equal(1, job.RetryCount);
        Assert.Null(job.OwningWorkerId);
        Assert.Null(job.NextRetryAtUtc);
    }

    [Fact]
    public async Task History_preserves_order_identity_nullable_fields_and_all_outcomes()
    {
        string[] outcomes = ["Running", "Succeeded", "Failed", "TimedOut", "Cancelled", "Abandoned", "PermanentlyFailed"];
        string json = JsonSerializer.Serialize(outcomes.Select((outcome, index) => new
        {
            jobId = JobId, attemptId = Guid.NewGuid(), attemptNumber = index + 1, workerId = "worker", outcome,
            startedAtUtc = "2026-10-01T12:00:00Z", deadlineAtUtc = "2026-10-01T12:00:30Z",
            finishedAtUtc = index == 0 ? null : "2026-10-01T12:00:01Z", durationMilliseconds = index == 0 ? (long?)null : 1000,
            errorCode = index > 1 ? "Error" : null, errorMessage = index > 1 ? "Failure." : null
        }));
        using HttpClient http = new(new StubHandler((_, _) => Task.FromResult(Json(json))));

        IReadOnlyList<JobAttemptResponse> history = await Client(http).GetAttemptsAsync(JobId);

        Assert.Equal(outcomes, history.Select(attempt => attempt.Outcome.ToString()));
        Assert.Equal(Enumerable.Range(1, 7), history.Select(attempt => attempt.AttemptNumber));
        Assert.Equal(7, history.Select(attempt => attempt.AttemptId).Distinct().Count());
        Assert.Null(history[0].FinishedAtUtc);
        Assert.Null(history[0].DurationMilliseconds);
        Assert.Equal(1000, history[1].DurationMilliseconds);
        Assert.Equal("Error", history[2].ErrorCode);
    }

    [Theory]
    [InlineData("AttemptTimedOut")]
    [InlineData("FutureServerReason")]
    public async Task Problem_details_keep_exact_reason_codes_and_extensions_without_retries(string code)
    {
        string raw = $$$"""{"status":409,"code":"{{{code}}}","title":"Conflict","detail":"Report rejected.","traceId":"trace-1","newField":{"value":7}}""";
        int calls = 0;
        using HttpClient http = new(new StubHandler((_, _) =>
        {
            calls++;
            return Task.FromResult(Json(raw, HttpStatusCode.Conflict));
        }));

        TaskForgeApiException error = await Assert.ThrowsAsync<TaskForgeApiException>(() => Client(http).CompleteAsync(JobId, AttemptId, new("worker")));

        Assert.Equal(HttpStatusCode.Conflict, error.StatusCode);
        Assert.Equal(code, error.Error.Code);
        Assert.Equal("Report rejected.", error.Error.Detail);
        Assert.Equal("trace-1", error.Error.TraceId);
        Assert.Equal(7, error.Error.AdditionalProperties["newField"].GetProperty("value").GetInt32());
        Assert.Equal(raw, error.ResponseBody);
        Assert.Equal(1, calls);
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound, "{\"message\":\"Job missing.\"}")]
    [InlineData(HttpStatusCode.Conflict, "{\"message\":\"Already completed.\",\"status\":\"Completed\",\"idempotencyKey\":\"key\"}")]
    [InlineData(HttpStatusCode.BadRequest, "{\"title\":\"Invalid request\",\"errors\":{\"Type\":[\"Required.\"]}}")]
    [InlineData(HttpStatusCode.InternalServerError, "{\"title\":\"Server error\"}")]
    [InlineData(HttpStatusCode.BadGateway, "<html>Proxy unavailable</html>")]
    [InlineData(HttpStatusCode.ServiceUnavailable, "")]
    [InlineData(HttpStatusCode.ServiceUnavailable, "null")]
    public async Task Every_error_shape_remains_a_typed_http_error(HttpStatusCode status, string raw)
    {
        using HttpClient http = new(new StubHandler((_, _) => Task.FromResult(Json(raw, status))));
        TaskForgeApiException error = await Assert.ThrowsAsync<TaskForgeApiException>(() => Client(http).GetJobAsync(JobId));

        Assert.Equal(status, error.StatusCode);
        Assert.Equal(raw, error.ResponseBody);
        if (status == HttpStatusCode.NotFound)
        {
            Assert.Equal("Job missing.", error.Error.Message);
        }
        else if (status == HttpStatusCode.BadRequest)
        {
            Assert.Equal("Required.", Assert.Single(error.Error.Errors["Type"]));
        }
        else if (status == HttpStatusCode.Conflict)
        {
            Assert.Equal("Completed", error.Error.AdditionalProperties["status"].GetString());
            Assert.Equal("key", error.Error.AdditionalProperties["idempotencyKey"].GetString());
        }
    }

    [Theory]
    [InlineData("submit")]
    [InlineData("wait")]
    [InlineData("complete")]
    [InlineData("fail")]
    [InlineData("get")]
    [InlineData("history")]
    [InlineData("cancel")]
    public async Task Caller_cancellation_reaches_every_operation(string operation)
    {
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using HttpClient http = new(new StubHandler(async (_, token) =>
        {
            entered.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return Json(JobJson());
        }));
        using CancellationTokenSource cancellation = new();
        Task running = InvokeAsync(Client(http), operation, cancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task Caller_cancellation_covers_response_body_reads()
    {
        TaskCompletionSource reading = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using HttpClient http = new(new StubHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new WaitingStream(reading)) })));
        using CancellationTokenSource cancellation = new();
        Task running = Client(http).GetJobAsync(JobId, cancellation.Token);
        await reading.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task Request_timeout_covers_response_body_reads()
    {
        using HttpClient http = new(new StubHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(new WaitingStream(new(TaskCreationOptions.RunContinuationsAsynchronously)))
        })));
        TaskForgeClient client = new(http, new() { ApplicationId = "a-project", RequestTimeout = TimeSpan.FromMilliseconds(100) });

        TimeoutException error = await Assert.ThrowsAsync<TimeoutException>(() => client.GetJobAsync(JobId).WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.IsAssignableFrom<OperationCanceledException>(error.InnerException);
    }

    [Fact]
    public async Task Long_poll_timeout_extends_the_ordinary_request_budget()
    {
        using HttpClient http = new(new StubHandler(async (_, token) =>
        {
            await Task.Delay(200, token);
            return new(HttpStatusCode.NoContent);
        }));
        TaskForgeClient client = new(http, new() { ApplicationId = "a-project", RequestTimeout = TimeSpan.FromMilliseconds(50) });

        Assert.Null(await client.WaitAsync(new("worker", ["calculate"], 30)));
    }

    [Fact]
    public async Task A_short_injected_timeout_is_rejected_without_changing_shared_client_settings()
    {
        using HttpClient http = new(new StubHandler((_, _) => throw new InvalidOperationException("Must not send."))) { Timeout = TimeSpan.FromSeconds(30) };
        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(() => Client(http).WaitAsync(new("worker", ["calculate"], 30)));
        Assert.Contains("40 seconds", error.Message);
        Assert.Equal(TimeSpan.FromSeconds(30), http.Timeout);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("")]
    [InlineData("not-json")]
    public async Task Invalid_success_body_is_not_misreported_as_an_empty_wait(string body)
    {
        using HttpClient http = new(new StubHandler((_, _) => Task.FromResult(Json(body))));
        await Assert.ThrowsAsync<JsonException>(() => Client(http).WaitAsync(new("worker", ["calculate"])));
    }

    [Theory]
    [InlineData("relative", "a-project")]
    [InlineData("ftp://example.com", "a-project")]
    [InlineData("http://localhost:8275?query=1", "a-project")]
    [InlineData("http://localhost:8275#fragment", "a-project")]
    [InlineData("http://localhost:8275", "")]
    [InlineData("http://localhost:8275", "invalid application")]
    public void Invalid_configuration_is_rejected(string url, string application)
    {
        using HttpClient http = new();
        Assert.Throws<ArgumentException>(() => new TaskForgeClient(http, new() { BaseUrl = new Uri(url, UriKind.RelativeOrAbsolute), ApplicationId = application }));
    }

    [Fact]
    public void Sdk_assembly_has_no_server_assembly_dependencies()
    {
        Assert.DoesNotContain(typeof(TaskForgeClient).Assembly.GetReferencedAssemblies(), name => name.Name!.StartsWith("TaskForge.", StringComparison.Ordinal));
    }

    private static TaskForgeClient Client(HttpClient http) => new(http, new() { ApplicationId = "a-project" });

    private static Task InvokeAsync(TaskForgeClient client, string operation, CancellationToken token) => operation switch
    {
        "submit" => client.SubmitAsync(new("calculate", JsonSerializer.SerializeToElement(new { value = 1 })), cancellationToken: token),
        "wait" => client.WaitAsync(new("worker", ["calculate"]), token),
        "complete" => client.CompleteAsync(JobId, AttemptId, new("worker"), token),
        "fail" => client.FailAsync(JobId, AttemptId, new("worker", "Temporary", "Unavailable."), token),
        "get" => client.GetJobAsync(JobId, token),
        "history" => client.GetAttemptsAsync(JobId, token),
        "cancel" => client.CancelAsync(JobId, token),
        _ => throw new ArgumentOutOfRangeException(nameof(operation))
    };

    private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) => new(status)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json")
    };

    private static string JobJson(string status = "Completed") => $$"""
        {"id":"{{JobId}}","applicationId":"a-project","type":"calculate","payload":{"value":1},
         "idempotencyKey":"key-01","result":{"sum":42},"priority":"High","status":"{{status}}",
         "maxRetries":1,"retryCount":1,"timeoutSeconds":30,"cancellationRequested":false,
         "createdAtUtc":"2026-10-01T12:00:00Z","updatedAtUtc":"2026-10-01T12:00:01Z","queuedAtUtc":"2026-10-01T12:00:00Z",
         "startedAtUtc":"2026-10-01T12:00:00Z","completedAtUtc":"2026-10-01T12:00:01Z",
         "nextRetryAtUtc":null,"owningWorkerId":null,"leaseExpiresAtUtc":null,"lastError":null}
        """;

    private sealed class StubHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
    }

    private sealed class WaitingStream(TaskCompletionSource reading) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            reading.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }
    }
}
