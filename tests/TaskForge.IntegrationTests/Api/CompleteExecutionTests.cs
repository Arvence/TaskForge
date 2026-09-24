using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using TaskForge.Api.Jobs;
using TaskForge.Application.Jobs;
using TaskForge.Application.Jobs.Models;
using TaskForge.Domain.Jobs;
using TaskForge.Infrastructure.Persistence;

namespace TaskForge.IntegrationTests.Api;

[Collection("SQL Server")]
public sealed class CompleteExecutionTests(SqlServerFixture fixture) : SqlServerTest(fixture)
{
    [Theory]
    [InlineData(null)]
    [InlineData("null")]
    [InlineData("{ \"ok\" : true, \"items\" : [1, 2] }")]
    [InlineData("\"text\"")]
    [InlineData("42")]
    [InlineData("false")]
    public async Task Complete_returns_job_with_atomic_success_and_unchanged_duplicate(string? json)
    {
        JobExecutionAssignment assignment = await AssignAsync();
        await using WebApplication app = await CreateAppAsync();
        using HttpClient client = app.GetTestClient();

        using HttpResponseMessage response = await PostAsync(client, assignment, json);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        JsonElement body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Completed", body.GetProperty("status").GetString());
        Assert.Equal("test-app", body.GetProperty("applicationId").GetString());
        Assert.Equal(assignment.Job.Id, body.GetProperty("id").GetGuid());
        string? normalized = new ExecutionReport.Complete(json).ResultJson;
        Assert.Equal(normalized ?? "null", JsonSerializer.Serialize(body.GetProperty("result")));
        await using TaskForgeDbContext read = new(DatabaseOptions);
        Job job = await read.Jobs.AsNoTracking().SingleAsync();
        JobAttempt attempt = await read.JobAttempts.AsNoTracking().SingleAsync();
        Assert.Equal(normalized, job.ResultJson);
        Assert.Equal(JobAttemptOutcome.Succeeded, attempt.Outcome);
        Assert.Equal(job.CompletedAtUtc, attempt.FinishedAtUtc);
        Assert.Equal(job.UpdatedAtUtc, attempt.FinishedAtUtc);
        Assert.Equal(assignment.Job.Version + 1, job.Version);
        Assert.Equal(0, job.RetryCount);
        string snapshot = await SnapshotAsync();

        using HttpResponseMessage duplicate = await PostAsync(client, assignment, json);
        Assert.Equal(HttpStatusCode.OK, duplicate.StatusCode);
        Assert.Equal(await response.Content.ReadAsStringAsync(), await duplicate.Content.ReadAsStringAsync());
        Assert.Equal(snapshot, await SnapshotAsync());
    }

    [Theory]
    [InlineData(3999)]
    [InlineData(4000)]
    [InlineData(4001)]
    public async Task Serialized_result_boundary_is_enforced_without_truncation(int length)
    {
        JobExecutionAssignment assignment = await AssignAsync();
        string snapshot = await SnapshotAsync();
        await using WebApplication app = await CreateAppAsync();
        using HttpClient client = app.GetTestClient();
        string json = "\"" + new string('x', length - 2) + "\"";

        using HttpResponseMessage response = await PostAsync(client, assignment, json);

        if (length <= 4000)
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            await using TaskForgeDbContext read = new(DatabaseOptions);
            Assert.Equal(json, (await read.Jobs.SingleAsync()).ResultJson);
            Assert.Equal(JobAttemptOutcome.Succeeded, (await read.JobAttempts.SingleAsync()).Outcome);
        }
        else
        {
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            JsonElement problem = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Contains("4000", problem.GetProperty("errors").GetProperty("Result")[0].GetString());
            Assert.Equal(snapshot, await SnapshotAsync());
        }
    }

    [Theory]
    [InlineData("{}", "[]")]
    [InlineData(null, "false")]
    [InlineData("{\"a\":1,\"b\":2}", "{\"b\":2,\"a\":1}")]
    [InlineData("1", "1.0")]
    public async Task Conflicting_complete_returns_409_without_changes(string? first, string conflicting)
    {
        JobExecutionAssignment assignment = await AssignAsync();
        await using WebApplication app = await CreateAppAsync();
        using HttpClient client = app.GetTestClient();
        using HttpResponseMessage accepted = await PostAsync(client, assignment, first);
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        string snapshot = await SnapshotAsync();

        using HttpResponseMessage response = await PostAsync(client, assignment, conflicting);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(snapshot, await SnapshotAsync());
    }

    [Theory]
    [InlineData(null, "worker-01", HttpStatusCode.BadRequest)]
    [InlineData("bad app", "worker-01", HttpStatusCode.BadRequest)]
    [InlineData("test-app", null, HttpStatusCode.BadRequest)]
    [InlineData("test-app", " ", HttpStatusCode.BadRequest)]
    [InlineData("other-app", "worker-01", HttpStatusCode.NotFound)]
    [InlineData("test-app", "WORKER-01", HttpStatusCode.Conflict)]
    [InlineData("test-app", "worker-01 ", HttpStatusCode.Conflict)]
    public async Task Invalid_or_mismatched_identity_cannot_complete(string? applicationId, string? workerId, HttpStatusCode expected)
    {
        JobExecutionAssignment assignment = await AssignAsync();
        string snapshot = await SnapshotAsync();
        await using WebApplication app = await CreateAppAsync();
        using HttpClient client = app.GetTestClient();

        using HttpResponseMessage response = await client.PostAsJsonAsync(Route(assignment), new { applicationId, workerId, result = new { ok = true } });

        Assert.Equal(expected, response.StatusCode);
        Assert.Equal(snapshot, await SnapshotAsync());
    }

    [Fact]
    public async Task Invalid_json_returns_400_without_changes()
    {
        JobExecutionAssignment assignment = await AssignAsync();
        string snapshot = await SnapshotAsync();
        await using WebApplication app = await CreateAppAsync();
        using HttpClient client = app.GetTestClient();

        using HttpResponseMessage response = await PostAsync(client, assignment, "{\"invalid\":}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(snapshot, await SnapshotAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Late_or_cancelled_first_success_returns_409_and_never_persists_result(bool cancelled)
    {
        JobExecutionAssignment assignment = await AssignAsync(expired: !cancelled);
        if (cancelled)
        {
            await using TaskForgeDbContext context = new(DatabaseOptions);
            Job job = await context.Jobs.SingleAsync();
            job.RequestCancellation(DateTimeOffset.UtcNow);
            await context.SaveChangesAsync();
        }

        await using WebApplication app = await CreateAppAsync();
        using HttpClient client = app.GetTestClient();
        using HttpResponseMessage response = await PostAsync(client, assignment, "{\"late\":true}");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        await using TaskForgeDbContext read = new(DatabaseOptions);
        Job persisted = await read.Jobs.SingleAsync();
        Assert.Null(persisted.ResultJson);
        Assert.Null(persisted.CompletedAtUtc);
        Assert.Equal(cancelled ? JobStatus.Cancelled : JobStatus.Retrying, persisted.Status);
        Assert.Equal(cancelled ? JobAttemptOutcome.Cancelled : JobAttemptOutcome.TimedOut, (await read.JobAttempts.SingleAsync()).Outcome);
        string snapshot = await SnapshotAsync();
        using HttpResponseMessage repeated = await PostAsync(client, assignment, "{}");
        Assert.Equal(HttpStatusCode.Conflict, repeated.StatusCode);
        Assert.Equal(snapshot, await SnapshotAsync());
    }

    [Fact]
    public async Task Stale_success_after_new_attempt_starts_returns_409_without_changes()
    {
        JobExecutionAssignment old = await AssignAsync(expired: true);
        await using (TaskForgeDbContext context = new(DatabaseOptions))
        {
            EfCoreJobStore store = new(context);
            DateTimeOffset now = DateTimeOffset.UtcNow;
            Assert.Equal(1, await store.RecoverExpiredLeasesAsync(now));
            JobExecutionAssignment? current = await store.TryDistributeAsync("test-app", "worker-01", ["external-only"], TimeSpan.FromSeconds(30), now);
            Assert.NotNull(current);
            Assert.NotEqual(old.AttemptId, current.AttemptId);
        }

        string snapshot = await SnapshotAsync();
        await using WebApplication app = await CreateAppAsync();
        using HttpClient client = app.GetTestClient();
        using HttpResponseMessage response = await PostAsync(client, old, "{}");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(snapshot, await SnapshotAsync());
    }

    [Fact]
    public async Task Normal_startup_does_not_register_execution_reporting()
    {
        await using WebApplicationFactory<Program> factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ConnectionStrings:TaskForge", ConnectionString);
            builder.UseSetting("Worker:Count", "0");
        });
        using HttpClient client = factory.CreateClient();
        using HttpResponseMessage response = await client.PostAsJsonAsync($"/api/jobs/{Guid.NewGuid()}/attempts/{Guid.NewGuid()}/complete", new { applicationId = "test-app", workerId = "worker-01" });
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        using HttpResponseMessage failure = await client.PostAsJsonAsync($"/api/jobs/{Guid.NewGuid()}/attempts/{Guid.NewGuid()}/fail", new { applicationId = "test-app", workerId = "worker-01", errorCode = "Failure", errorMessage = "Failed." });
        Assert.Equal(HttpStatusCode.NotFound, failure.StatusCode);
        Assert.Null(factory.Services.GetService<JobExecutionService>());
    }

    private async Task<WebApplication> CreateAppAsync()
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.ConfigureHttpJsonOptions(options => options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
        builder.Services.AddTaskForgeSqlServer(ConnectionString);
        builder.Services.AddOptions();
        builder.Services.AddSingleton<JobRetryPolicy>();
        builder.Services.AddTaskForgeExecutionReporting();
        WebApplication app = builder.Build();
        app.MapTaskForgeExecutionEndpoints();
        await app.StartAsync();
        return app;
    }

    private async Task<JobExecutionAssignment> AssignAsync(bool expired = false)
    {
        await using TaskForgeDbContext context = new(DatabaseOptions);
        DateTimeOffset now = await context.Database.SqlQuery<DateTimeOffset>($"SELECT TODATETIMEOFFSET(SYSUTCDATETIME(), '+00:00') AS [Value]").SingleAsync();
        DateTimeOffset start = expired ? now.AddMinutes(-10) : now;
        Job job = new(Guid.NewGuid(), "test-app", "external-only", "{}", JobPriority.Normal, 3, 300, start);
        job.Queue(start);
        EfCoreJobStore store = new(context);
        await store.AddAsync(job);
        return (await store.TryDistributeAsync("test-app", "worker-01", ["external-only"], TimeSpan.FromSeconds(30), start))!;
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

    private static string Route(JobExecutionAssignment assignment) => $"/api/jobs/{assignment.Job.Id}/attempts/{assignment.AttemptId}/complete";

    private static Task<HttpResponseMessage> PostAsync(HttpClient client, JobExecutionAssignment assignment, string? result)
    {
        string json = "{\"applicationId\":\" TEST-APP \",\"workerId\":\"worker-01\"" + (result is null ? "" : ",\"result\":" + result) + "}";
        return client.PostAsync(Route(assignment), new StringContent(json, Encoding.UTF8, "application/json"));
    }
}
