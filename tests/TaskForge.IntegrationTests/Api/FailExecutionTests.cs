using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

using TaskForge.Api.Jobs;
using TaskForge.Application.Jobs;
using TaskForge.Application.Jobs.Models;
using TaskForge.Application.Workers;
using TaskForge.Domain.Jobs;
using TaskForge.Infrastructure.Persistence;

namespace TaskForge.IntegrationTests.Api;

[Collection("SQL Server")]
public sealed class FailExecutionTests(SqlServerFixture fixture) : SqlServerTest(fixture)
{
    [Theory]
    [InlineData(" Failure ", 3, false)]
    [InlineData("UnrecognizedCode", 3, false)]
    [InlineData("UnrecognizedCode", 0, false)]
    [InlineData("InvalidPayload", 3, true)]
    [InlineData("UnsupportedJobType", 3, true)]
    [InlineData("NonRetryableJobException", 3, true)]
    [InlineData("Timeout", 3, false)]
    [InlineData("CancellationRequested", 3, false)]
    public async Task Failure_uses_server_policy_and_ignores_client_retry_fields(string code, int maxRetries, bool permanent)
    {
        JobExecutionAssignment assignment = await AssignAsync(maxRetries: maxRetries);
        await using WebApplication app = await CreateAppAsync();
        using HttpClient client = app.GetTestClient();
        DateTimeOffset before = await SqlNowAsync();

        using HttpResponseMessage response = await client.PostAsJsonAsync(Route(assignment), new
        {
            applicationId = " TEST-APP ", workerId = "worker-01", errorCode = code, errorMessage = " Failed. ",
            retryable = permanent, status = "Completed", requestedJobStatus = "Cancelled", retryCount = 999,
            maxRetries = 999, nextRetryAtUtc = "2099-01-01T00:00:00Z", retryDelaySeconds = 999,
            outcome = "Succeeded", finishedAtUtc = "2000-01-01T00:00:00Z"
        });

        DateTimeOffset after = await SqlNowAsync();
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        JobStatus expectedStatus = permanent || maxRetries == 0 ? JobStatus.DeadLettered : JobStatus.Retrying;
        JsonElement body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(expectedStatus.ToString(), body.GetProperty("status").GetString());
        await using TaskForgeDbContext read = new(DatabaseOptions);
        Job job = await read.Jobs.AsNoTracking().SingleAsync();
        JobAttempt attempt = await read.JobAttempts.AsNoTracking().SingleAsync();
        Assert.Equal(expectedStatus, job.Status);
        Assert.Equal(permanent ? JobAttemptOutcome.PermanentlyFailed : JobAttemptOutcome.Failed, attempt.Outcome);
        Assert.Equal(code.Trim().ToLowerInvariant(), attempt.ErrorCode);
        Assert.Equal("Failed.", attempt.ErrorMessage);
        Assert.Equal(permanent ? 0 : 1, job.RetryCount);
        Assert.Equal(maxRetries, job.MaxRetries);
        Assert.Equal(assignment.Job.Version + 1, job.Version);
        Assert.InRange(attempt.FinishedAtUtc!.Value, before, after);
        Assert.Equal(attempt.FinishedAtUtc, job.UpdatedAtUtc);
        Assert.Equal(expectedStatus == JobStatus.Retrying ? attempt.FinishedAtUtc.Value.AddSeconds(1) : (DateTimeOffset?)null, job.NextRetryAtUtc);
        Assert.Null(job.ResultJson);
        Assert.Null(job.OwningWorkerId);
        Assert.Null(job.LeaseExpiresAtUtc);
        string snapshot = await SnapshotAsync();

        using HttpResponseMessage duplicate = await PostAsync(client, assignment, code.Trim().ToUpperInvariant(), "Failed.");
        Assert.Equal(HttpStatusCode.OK, duplicate.StatusCode);
        Assert.Equal(snapshot, await SnapshotAsync());
    }

    [Theory]
    [InlineData(99, 3999, HttpStatusCode.OK)]
    [InlineData(100, 4000, HttpStatusCode.OK)]
    [InlineData(101, 4000, HttpStatusCode.BadRequest)]
    [InlineData(100, 4001, HttpStatusCode.BadRequest)]
    public async Task Failure_fields_respect_storage_boundaries(int codeLength, int messageLength, HttpStatusCode expected)
    {
        JobExecutionAssignment assignment = await AssignAsync();
        string snapshot = await SnapshotAsync();
        await using WebApplication app = await CreateAppAsync();
        using HttpClient client = app.GetTestClient();
        string code = new('c', codeLength), message = new('m', messageLength);

        using HttpResponseMessage response = await PostAsync(client, assignment, " " + code + " ", " " + message + " ");

        Assert.Equal(expected, response.StatusCode);
        if (expected == HttpStatusCode.OK)
        {
            await using TaskForgeDbContext read = new(DatabaseOptions);
            JobAttempt attempt = await read.JobAttempts.SingleAsync();
            Assert.Equal(code, attempt.ErrorCode);
            Assert.Equal(message, attempt.ErrorMessage);
            Assert.Equal(4000, (await read.Jobs.SingleAsync()).LastError!.Length);
        }
        else
        {
            JsonElement problem = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.True(problem.GetProperty("errors").TryGetProperty(codeLength > 100 ? "ErrorCode" : "ErrorMessage", out _));
            Assert.Equal(snapshot, await SnapshotAsync());
        }
    }

    [Theory]
    [InlineData(null, "Failed.", "ErrorCode")]
    [InlineData(" ", "Failed.", "ErrorCode")]
    [InlineData("Failure", null, "ErrorMessage")]
    [InlineData("Failure", " ", "ErrorMessage")]
    public async Task Invalid_failure_fields_return_validation_errors(string? code, string? message, string field)
    {
        JobExecutionAssignment assignment = await AssignAsync();
        string snapshot = await SnapshotAsync();
        await using WebApplication app = await CreateAppAsync();
        using HttpClient client = app.GetTestClient();

        using HttpResponseMessage response = await PostAsync(client, assignment, code, message);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        JsonElement body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(body.GetProperty("errors").TryGetProperty(field, out _));
        Assert.Equal(snapshot, await SnapshotAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Duplicate_after_redistribution_returns_current_job_and_preserves_all_state(bool completeNext)
    {
        JobExecutionAssignment old = await AssignAsync();
        await using WebApplication app = await CreateAppAsync();
        using HttpClient client = app.GetTestClient();
        using HttpResponseMessage first = await PostAsync(client, old, "Failure", "Failed.");
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        JobExecutionAssignment current;
        await using (TaskForgeDbContext context = new(DatabaseOptions))
        {
            Job job = await context.Jobs.AsNoTracking().SingleAsync();
            TimeSpan delay = job.NextRetryAtUtc!.Value - await SqlNowAsync();
            if (delay > TimeSpan.Zero)
            {
                await Task.Delay(delay + TimeSpan.FromMilliseconds(50));
            }

            current = (await new EfCoreJobStore(context).TryDistributeAsync("test-app", "new-worker", ["external-only"], TimeSpan.FromSeconds(30), await SqlNowAsync()))!;
            Assert.NotNull(current);
        }

        if (completeNext)
        {
            using HttpResponseMessage complete = await client.PostAsJsonAsync(Route(current, "complete"), new { applicationId = "test-app", workerId = "new-worker", result = new { ok = true } });
            Assert.Equal(HttpStatusCode.OK, complete.StatusCode);
        }

        string snapshot = await SnapshotAsync();
        using HttpResponseMessage duplicate = await PostAsync(client, old, " FAILURE ", " Failed. ");
        Assert.Equal(HttpStatusCode.OK, duplicate.StatusCode);
        JsonElement body = await duplicate.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(completeNext ? "Completed" : "Processing", body.GetProperty("status").GetString());
        using HttpResponseMessage changedMessage = await PostAsync(client, old, "failure", "Changed.");
        using HttpResponseMessage changedCode = await PostAsync(client, old, "Other", "Failed.");
        using HttpResponseMessage wrongWorker = await client.PostAsJsonAsync(Route(old), new { applicationId = "test-app", workerId = "new-worker", errorCode = "Failure", errorMessage = "Failed." });
        using HttpResponseMessage completeOld = await client.PostAsJsonAsync(Route(old, "complete"), new { applicationId = "test-app", workerId = "worker-01" });
        Assert.Equal(HttpStatusCode.Conflict, changedMessage.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, changedCode.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, wrongWorker.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, completeOld.StatusCode);
        Assert.Equal(snapshot, await SnapshotAsync());
    }

    [Fact]
    public async Task Failure_cannot_overwrite_accepted_completion()
    {
        JobExecutionAssignment assignment = await AssignAsync();
        await using WebApplication app = await CreateAppAsync();
        using HttpClient client = app.GetTestClient();
        using HttpResponseMessage complete = await client.PostAsJsonAsync(Route(assignment, "complete"), new { applicationId = "test-app", workerId = "worker-01" });
        Assert.Equal(HttpStatusCode.OK, complete.StatusCode);
        string snapshot = await SnapshotAsync();

        using HttpResponseMessage failure = await PostAsync(client, assignment, "Failure", "Failed.");

        Assert.Equal(HttpStatusCode.Conflict, failure.StatusCode);
        Assert.Equal(snapshot, await SnapshotAsync());
    }

    [Theory]
    [InlineData(null, "worker-01", HttpStatusCode.BadRequest)]
    [InlineData("test-app", " ", HttpStatusCode.BadRequest)]
    [InlineData("other-app", "worker-01", HttpStatusCode.NotFound)]
    [InlineData("test-app", "WORKER-01", HttpStatusCode.Conflict)]
    public async Task Failure_requires_matching_identity(string? applicationId, string? workerId, HttpStatusCode expected)
    {
        JobExecutionAssignment assignment = await AssignAsync();
        string snapshot = await SnapshotAsync();
        await using WebApplication app = await CreateAppAsync();
        using HttpClient client = app.GetTestClient();

        using HttpResponseMessage response = await client.PostAsJsonAsync(Route(assignment), new { applicationId, workerId, errorCode = "Failure", errorMessage = "Failed." });

        Assert.Equal(expected, response.StatusCode);
        Assert.Equal(snapshot, await SnapshotAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Failure_cannot_override_timeout_or_requested_cancellation(bool cancelled)
    {
        JobExecutionAssignment assignment = await AssignAsync(expired: !cancelled);
        if (cancelled)
        {
            await using TaskForgeDbContext context = new(DatabaseOptions);
            Job job = await context.Jobs.SingleAsync();
            job.RequestCancellation(await SqlNowAsync());
            await context.SaveChangesAsync();
        }

        await using WebApplication app = await CreateAppAsync();
        using HttpClient client = app.GetTestClient();
        using HttpResponseMessage response = await PostAsync(client, assignment, "InvalidPayload", "Permanent client failure.");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        await using TaskForgeDbContext read = new(DatabaseOptions);
        Assert.Equal(cancelled ? JobStatus.Cancelled : JobStatus.Retrying, (await read.Jobs.SingleAsync()).Status);
        Assert.Equal(cancelled ? JobAttemptOutcome.Cancelled : JobAttemptOutcome.TimedOut, (await read.JobAttempts.SingleAsync()).Outcome);
        string snapshot = await SnapshotAsync();
        using HttpResponseMessage repeated = await PostAsync(client, assignment, "InvalidPayload", "Permanent client failure.");
        Assert.Equal(HttpStatusCode.Conflict, repeated.StatusCode);
        Assert.Equal(snapshot, await SnapshotAsync());
    }

    private async Task<WebApplication> CreateAppAsync()
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.ConfigureHttpJsonOptions(options => options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
        builder.Services.AddTaskForgeSqlServer(ConnectionString);
        builder.Services.AddSingleton(new JobRetryPolicy(Options.Create(new WorkerOptions { RetryDelaySeconds = 1, MaxRetryDelaySeconds = 3 })));
        builder.Services.AddTaskForgeExecutionReporting();
        WebApplication app = builder.Build();
        app.MapTaskForgeExecutionEndpoints();
        await app.StartAsync();
        return app;
    }

    private async Task<JobExecutionAssignment> AssignAsync(int maxRetries = 3, bool expired = false)
    {
        await using TaskForgeDbContext context = new(DatabaseOptions);
        DateTimeOffset now = (await SqlNowAsync()).AddMinutes(expired ? -10 : 0);
        Job job = new(Guid.NewGuid(), "test-app", "external-only", "{}", JobPriority.Normal, maxRetries, 300, now);
        job.Queue(now);
        EfCoreJobStore store = new(context);
        await store.AddAsync(job);
        return (await store.TryDistributeAsync("test-app", "worker-01", ["external-only"], TimeSpan.FromSeconds(30), now))!;
    }

    private async Task<DateTimeOffset> SqlNowAsync()
    {
        await using TaskForgeDbContext context = new(DatabaseOptions);
        return await context.Database.SqlQuery<DateTimeOffset>($"SELECT TODATETIMEOFFSET(SYSUTCDATETIME(), '+00:00') AS [Value]").SingleAsync();
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

    private static string Route(JobExecutionAssignment assignment, string operation = "fail") => $"/api/jobs/{assignment.Job.Id}/attempts/{assignment.AttemptId}/{operation}";

    private static Task<HttpResponseMessage> PostAsync(HttpClient client, JobExecutionAssignment assignment, string? code, string? message) =>
        client.PostAsJsonAsync(Route(assignment), new { applicationId = "test-app", workerId = "worker-01", errorCode = code, errorMessage = message });
}
