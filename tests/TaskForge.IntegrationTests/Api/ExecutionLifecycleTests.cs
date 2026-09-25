using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;

using TaskForge.Api.Jobs;
using TaskForge.Infrastructure.Persistence;

namespace TaskForge.IntegrationTests.Api;

[Collection("SQL Server")]
public sealed class ExecutionLifecycleTests(SqlServerFixture fixture) : SqlServerTest(fixture)
{
    [Theory]
    [InlineData("complete")]
    [InlineData("fail")]
    public async Task Reports_validate_application_and_preserve_exact_duplicates_through_normal_startup(string operation)
    {
        await using WebApplicationFactory<Program> factory = CreateFactory();
        using HttpClient client = factory.CreateClient();
        Guid jobId = await SubmitAsync(client, maxRetries: 0);
        ExecutionAssignmentResponse assignment = await WaitAsync(client);
        Assert.Equal(jobId, assignment.JobId);
        JsonElement running = Assert.Single((await HistoryAsync(client, jobId)).EnumerateArray());
        AssertHistory(running, assignment, "Running");
        string before = await SnapshotAsync();

        using HttpResponseMessage wrongApplication = await ReportAsync(client, assignment, operation, "other-app");
        await AssertProblemAsync(wrongApplication, HttpStatusCode.NotFound);
        Assert.Equal(before, await SnapshotAsync());

        using HttpResponseMessage accepted = await ReportAsync(client, assignment, operation);
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        string persisted = await SnapshotAsync();
        using HttpResponseMessage duplicate = await ReportAsync(client, assignment, operation);
        Assert.Equal(HttpStatusCode.OK, duplicate.StatusCode);
        Assert.Equal(await accepted.Content.ReadAsStringAsync(), await duplicate.Content.ReadAsStringAsync());
        Assert.Equal(persisted, await SnapshotAsync());

        JsonElement job = await client.GetFromJsonAsync<JsonElement>($"/api/jobs/{jobId}");
        Assert.Equal(operation == "complete" ? "Completed" : "DeadLettered", job.GetProperty("status").GetString());
        AssertHistory(Assert.Single((await HistoryAsync(client, jobId)).EnumerateArray()), assignment, operation == "complete" ? "Succeeded" : "Failed");
    }

    [Theory]
    [InlineData("complete")]
    [InlineData("fail")]
    public async Task Expired_execution_is_redistributed_and_stale_reports_cannot_mutate_current_history(string operation)
    {
        await using WebApplicationFactory<Program> factory = CreateFactory();
        using HttpClient client = factory.CreateClient();
        Guid jobId = await SubmitAsync(client, timeoutSeconds: 2);
        ExecutionAssignmentResponse old = await WaitAsync(client);
        ExecutionAssignmentResponse current = await WaitAsync(client, "replacement-worker", 10);
        Assert.Equal(jobId, current.JobId);
        Assert.NotEqual(old.AttemptId, current.AttemptId);
        Assert.Equal(2, current.AttemptNumber);
        string before = await SnapshotAsync();

        using HttpResponseMessage stale = await ReportAsync(client, old, operation);
        await AssertProblemAsync(stale, HttpStatusCode.Conflict);
        Assert.Equal(before, await SnapshotAsync());

        using HttpResponseMessage completed = await ReportAsync(client, current, "complete");
        Assert.Equal(HttpStatusCode.OK, completed.StatusCode);
        JsonElement job = await client.GetFromJsonAsync<JsonElement>($"/api/jobs/{jobId}");
        Assert.Equal("Completed", job.GetProperty("status").GetString());
        Assert.Equal(1, job.GetProperty("retryCount").GetInt32());
        JsonElement[] history = (await HistoryAsync(client, jobId)).EnumerateArray().ToArray();
        Assert.Equal(2, history.Length);
        AssertHistory(history[0], old, "TimedOut");
        AssertHistory(history[1], current, "Succeeded");
    }

    [Fact]
    public async Task Retry_exhaustion_historical_duplicate_listing_statistics_and_replay_work_over_http()
    {
        await using WebApplicationFactory<Program> factory = CreateFactory();
        using HttpClient client = factory.CreateClient();
        Guid jobId = await SubmitAsync(client);
        ExecutionAssignmentResponse first = await WaitAsync(client);
        using HttpResponseMessage failed = await ReportAsync(client, first, "fail");
        Assert.Equal(HttpStatusCode.OK, failed.StatusCode);
        Assert.Equal("Retrying", (await failed.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("status").GetString());
        ExecutionAssignmentResponse second = await WaitAsync(client, waitSeconds: 5);
        Assert.Equal(2, second.AttemptNumber);
        string processing = await SnapshotAsync();
        using HttpResponseMessage historicalDuplicate = await ReportAsync(client, first, "fail");
        Assert.Equal(HttpStatusCode.OK, historicalDuplicate.StatusCode);
        Assert.Equal(processing, await SnapshotAsync());
        using HttpResponseMessage exhausted = await ReportAsync(client, second, "fail");
        Assert.Equal(HttpStatusCode.OK, exhausted.StatusCode);
        JsonElement terminal = await exhausted.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("DeadLettered", terminal.GetProperty("status").GetString());
        Assert.Equal(2, terminal.GetProperty("retryCount").GetInt32());
        JsonElement[] history = (await HistoryAsync(client, jobId)).EnumerateArray().ToArray();
        Assert.Equal(2, history.Length);
        AssertHistory(history[0], first, "Failed");
        AssertHistory(history[1], second, "Failed");
        string originalHistory = JsonSerializer.Serialize(history);

        JsonElement page = await client.GetFromJsonAsync<JsonElement>("/api/jobs?applicationId=test-app&status=DeadLettered&type=client-task&pageSize=1");
        Assert.Equal(1, page.GetProperty("totalCount").GetInt32());
        Assert.Equal(jobId, Assert.Single(page.GetProperty("items").EnumerateArray()).GetProperty("id").GetGuid());
        JsonElement stats = await client.GetFromJsonAsync<JsonElement>("/api/stats");
        Assert.Equal(1, stats.GetProperty("totalJobs").GetInt32());
        Assert.Equal(1, stats.GetProperty("countsByStatus").GetProperty("deadLettered").GetInt32());
        Assert.Equal(0, stats.GetProperty("countsByStatus").GetProperty("retrying").GetInt32());

        using HttpResponseMessage replay = await client.PostAsync($"/api/jobs/{jobId}/retry", null);
        Assert.Equal(HttpStatusCode.Created, replay.StatusCode);
        Guid replayId = (await replay.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        Assert.NotEqual(jobId, replayId);
        ExecutionAssignmentResponse replayAssignment = await WaitAsync(client);
        Assert.Equal(replayId, replayAssignment.JobId);
        Assert.Equal(1, replayAssignment.AttemptNumber);
        using HttpResponseMessage completed = await ReportAsync(client, replayAssignment, "complete");
        Assert.Equal(HttpStatusCode.OK, completed.StatusCode);
        Assert.Equal(originalHistory, JsonSerializer.Serialize((await HistoryAsync(client, jobId)).EnumerateArray().ToArray()));
        Assert.Equal(terminal.GetRawText(), (await client.GetFromJsonAsync<JsonElement>($"/api/jobs/{jobId}")).GetRawText());
        stats = await client.GetFromJsonAsync<JsonElement>("/api/stats");
        Assert.Equal(2, stats.GetProperty("totalJobs").GetInt32());
        Assert.Equal(1, stats.GetProperty("countsByStatus").GetProperty("completed").GetInt32());
        Assert.Equal(50m, stats.GetProperty("successRatePercent").GetDecimal());
        using HttpResponseMessage ready = await client.GetAsync("/api/ready");
        Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
    }

    [Theory]
    [InlineData("complete")]
    [InlineData("fail")]
    public async Task Concurrent_report_and_cancellation_preserve_one_finished_attempt(string operation)
    {
        await using WebApplicationFactory<Program> factory = CreateFactory();
        using HttpClient client = factory.CreateClient();
        Guid jobId = await SubmitAsync(client);
        ExecutionAssignmentResponse assignment = await WaitAsync(client);
        Task<HttpResponseMessage> reportTask = ReportAsync(client, assignment, operation);
        Task<HttpResponseMessage> cancelTask = client.PostAsync($"/api/jobs/{jobId}/cancel", null);
        await Task.WhenAll(reportTask, cancelTask);
        using HttpResponseMessage report = await reportTask;
        using HttpResponseMessage cancel = await cancelTask;
        JsonElement job = await client.GetFromJsonAsync<JsonElement>($"/api/jobs/{jobId}");
        JsonElement attempt = Assert.Single((await HistoryAsync(client, jobId)).EnumerateArray());

        if (operation == "complete" && report.StatusCode == HttpStatusCode.OK)
        {
            Assert.Equal(HttpStatusCode.Conflict, cancel.StatusCode);
            Assert.Equal("Completed", job.GetProperty("status").GetString());
            AssertHistory(attempt, assignment, "Succeeded");
            Assert.Equal(0, job.GetProperty("retryCount").GetInt32());
        }
        else
        {
            Assert.Equal(HttpStatusCode.OK, cancel.StatusCode);
            Assert.Equal("Cancelled", job.GetProperty("status").GetString());
            Assert.Equal(JsonValueKind.Null, job.GetProperty("result").ValueKind);
            bool failureWon = operation == "fail" && report.StatusCode == HttpStatusCode.OK;
            AssertHistory(attempt, assignment, failureWon ? "Failed" : "Cancelled");
            Assert.Equal(failureWon ? 1 : 0, job.GetProperty("retryCount").GetInt32());
            if (!failureWon)
            {
                await AssertProblemAsync(report, HttpStatusCode.Conflict);
            }
        }

        Assert.NotEqual(JsonValueKind.Null, attempt.GetProperty("finishedAtUtc").ValueKind);
        string snapshot = await SnapshotAsync();
        using HttpResponseMessage repeated = await ReportAsync(client, assignment, operation);
        Assert.Contains(repeated.StatusCode, new[] { HttpStatusCode.OK, HttpStatusCode.Conflict });
        Assert.Equal(snapshot, await SnapshotAsync());
    }

    [Theory]
    [InlineData("/api/jobs")]
    [InlineData("/api/executions/wait")]
    [InlineData("/api/jobs/11111111-1111-1111-1111-111111111111/attempts/22222222-2222-2222-2222-222222222222/complete")]
    [InlineData("/api/jobs/11111111-1111-1111-1111-111111111111/attempts/22222222-2222-2222-2222-222222222222/fail")]
    public async Task Binding_errors_have_problem_details_and_do_not_write_state(string route)
    {
        await using WebApplicationFactory<Program> factory = CreateFactory();
        using HttpClient client = factory.CreateClient();
        using StringContent malformed = new("{", Encoding.UTF8, "application/json");
        using HttpResponseMessage badJson = await client.PostAsync(route, malformed);
        await AssertProblemAsync(badJson, HttpStatusCode.BadRequest);
        using StringContent wrongMedia = new("{}", Encoding.UTF8, "text/plain");
        using HttpResponseMessage unsupported = await client.PostAsync(route, wrongMedia);
        await AssertProblemAsync(unsupported, HttpStatusCode.UnsupportedMediaType);
        using HttpResponseMessage invalid = await client.PostAsJsonAsync(route, new { });
        await AssertProblemAsync(invalid, HttpStatusCode.BadRequest);
        Assert.True((await invalid.Content.ReadFromJsonAsync<JsonElement>()).TryGetProperty("errors", out _));
        await using TaskForgeDbContext context = new(DatabaseOptions);
        Assert.Empty(await context.Jobs.ToArrayAsync());
        Assert.Empty(await context.JobAttempts.ToArrayAsync());
    }

    private WebApplicationFactory<Program> CreateFactory() => new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
    {
        builder.UseSetting("ConnectionStrings:TaskForge", ConnectionString);
        builder.UseSetting("Worker:RetryDelaySeconds", "1");
        builder.UseSetting("Worker:MaxRetryDelaySeconds", "1");
        builder.UseSetting("Worker:LeaseGraceSeconds", "1");
        builder.UseSetting("Worker:PollIntervalMilliseconds", "50");
    });

    private static async Task<Guid> SubmitAsync(HttpClient client, int maxRetries = 1, int timeoutSeconds = 30)
    {
        using HttpResponseMessage response = await client.PostAsJsonAsync("/api/jobs", new { applicationId = "test-app", type = "client-task", payload = new { value = 42 }, maxRetries, timeoutSeconds });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }

    private static async Task<ExecutionAssignmentResponse> WaitAsync(HttpClient client, string workerId = "remote-worker", int waitSeconds = 0)
    {
        using HttpResponseMessage response = await client.PostAsJsonAsync("/api/executions/wait", new { applicationId = "test-app", workerId, supportedTypes = new[] { "client-task" }, waitSeconds });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        return (await response.Content.ReadFromJsonAsync<ExecutionAssignmentResponse>())!;
    }

    private static Task<HttpResponseMessage> ReportAsync(HttpClient client, ExecutionAssignmentResponse assignment, string operation, string applicationId = "test-app") =>
        client.PostAsJsonAsync($"/api/jobs/{assignment.JobId}/attempts/{assignment.AttemptId}/{operation}", operation == "complete"
            ? (object)new { applicationId, assignment.WorkerId, result = new { ok = true } }
            : new { applicationId, assignment.WorkerId, errorCode = "Temporary", errorMessage = "Retry later." });

    private static Task<JsonElement> HistoryAsync(HttpClient client, Guid jobId) => client.GetFromJsonAsync<JsonElement>($"/api/jobs/{jobId}/attempts");

    private static void AssertHistory(JsonElement attempt, ExecutionAssignmentResponse assignment, string outcome)
    {
        Assert.Equal(assignment.JobId, attempt.GetProperty("jobId").GetGuid());
        Assert.Equal(assignment.AttemptId, attempt.GetProperty("attemptId").GetGuid());
        Assert.Equal(assignment.AttemptNumber, attempt.GetProperty("attemptNumber").GetInt32());
        Assert.Equal(assignment.WorkerId, attempt.GetProperty("workerId").GetString());
        Assert.Equal(assignment.StartedAtUtc, attempt.GetProperty("startedAtUtc").GetDateTimeOffset());
        Assert.Equal(assignment.DeadlineAtUtc, attempt.GetProperty("deadlineAtUtc").GetDateTimeOffset());
        Assert.Equal(assignment.StartedAtUtc.AddSeconds(assignment.TimeoutSeconds), assignment.DeadlineAtUtc);
        Assert.True(assignment.LeaseExpiresAtUtc > assignment.DeadlineAtUtc);
        Assert.Equal(outcome, attempt.GetProperty("outcome").GetString());
    }

    private static async Task AssertProblemAsync(HttpResponseMessage response, HttpStatusCode status)
    {
        Assert.Equal(status, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        JsonElement problem = JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync());
        Assert.Equal((int)status, problem.GetProperty("status").GetInt32());
        Assert.False(string.IsNullOrWhiteSpace(problem.GetProperty("title").GetString()));
    }

    private async Task<string> SnapshotAsync()
    {
        await using TaskForgeDbContext context = new(DatabaseOptions);
        return JsonSerializer.Serialize(new
        {
            Jobs = await context.Jobs.AsNoTracking().OrderBy(job => job.Id).ToArrayAsync(),
            Attempts = await context.JobAttempts.AsNoTracking().OrderBy(attempt => attempt.AttemptNumber).ToArrayAsync()
        });
    }
}
