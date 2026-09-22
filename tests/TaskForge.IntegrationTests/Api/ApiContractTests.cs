using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

using TaskForge.Infrastructure.Persistence;
using TaskForge.Domain.Jobs;
using TaskForge.Application.Workers;

namespace TaskForge.IntegrationTests.Api;

[Collection("SQL Server")]
public sealed class ApiContractTests(SqlServerFixture fixture) : SqlServerTest(fixture)
{
    [Fact]
    public async Task Generate_report_runs_through_registered_handler_and_persists_result_and_attempt()
    {
        await using WebApplicationFactory<Program> factory = CreateFactory();
        using HttpClient client = factory.CreateClient();
        using HttpResponseMessage submitted = await client.PostAsJsonAsync("/api/jobs", new
        {
            applicationId = "test-app",
            type = "generate-report",
            payload = new
            {
                title = "Expenses",
                entries = new[]
                {
                    new { category = "Travel", amount = 10.25m },
                    new { category = "Supplies", amount = 2m },
                    new { category = "Travel", amount = 0.75m }
                }
            },
            maxRetries = 3,
            timeoutSeconds = 30
        });
        Assert.Equal(HttpStatusCode.Created, submitted.StatusCode);
        JsonElement queued = await submitted.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Queued", queued.GetProperty("status").GetString());
        Guid id = queued.GetProperty("id").GetGuid();

        await using (AsyncServiceScope scope = factory.Services.CreateAsyncScope())
        {
            JobExecutor executor = scope.ServiceProvider.GetRequiredService<JobExecutor>();
            Assert.True(await executor.ProcessNextAsync("report-worker", _ => { }, CancellationToken.None, CancellationToken.None));
        }

        using HttpResponseMessage fetched = await client.GetAsync($"/api/jobs/{id}");
        Assert.Equal(HttpStatusCode.OK, fetched.StatusCode);
        JsonElement completed = await fetched.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Completed", completed.GetProperty("status").GetString());
        Assert.Equal(0, completed.GetProperty("retryCount").GetInt32());
        JsonElement result = completed.GetProperty("result");
        Assert.Equal("Expenses", result.GetProperty("title").GetString());
        Assert.Equal(3, result.GetProperty("entryCount").GetInt32());
        Assert.Equal(13m, result.GetProperty("totalAmount").GetDecimal());
        Assert.Equal(2, result.GetProperty("categories").GetArrayLength());

        using HttpResponseMessage history = await client.GetAsync($"/api/jobs/{id}/attempts");
        Assert.Equal(HttpStatusCode.OK, history.StatusCode);
        JsonElement attempt = Assert.Single((await history.Content.ReadFromJsonAsync<JsonElement>()).EnumerateArray());
        Assert.Equal("Succeeded", attempt.GetProperty("outcome").GetString());
        Assert.Equal(1, attempt.GetProperty("attemptNumber").GetInt32());
    }

    [Fact]
    public async Task Invalid_report_is_rejected_at_submission_without_persisting_job()
    {
        await using WebApplicationFactory<Program> factory = CreateFactory();
        using HttpClient client = factory.CreateClient();

        using HttpResponseMessage response = await client.PostAsJsonAsync("/api/jobs", new
        {
            applicationId = "test-app",
            type = "generate-report",
            payload = new { title = "Expenses", entries = new[] { new { category = "Travel", amount = -1m } } }
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        JsonElement problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains("amount", problem.GetProperty("errors").GetProperty("Payload")[0].GetString());
        await using TaskForgeDbContext context = new(DatabaseOptions);
        Assert.Empty(await context.Jobs.ToListAsync());
    }

    [Fact]
    public async Task Invalid_persisted_report_is_dead_lettered_without_retry()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        Job job = new(Guid.NewGuid(), "test-app", "generate-report", "{}", JobPriority.Normal, 3, 30, now);
        job.Queue(now);
        await using (TaskForgeDbContext context = new(DatabaseOptions))
        {
            context.Jobs.Add(job);
            await context.SaveChangesAsync();
        }

        await using WebApplicationFactory<Program> factory = CreateFactory();
        await using (AsyncServiceScope scope = factory.Services.CreateAsyncScope())
        {
            JobExecutor executor = scope.ServiceProvider.GetRequiredService<JobExecutor>();
            Assert.True(await executor.ProcessNextAsync("report-worker", _ => { }, CancellationToken.None, CancellationToken.None));
            Assert.False(await executor.ProcessNextAsync("report-worker", _ => { }, CancellationToken.None, CancellationToken.None));
        }

        await using TaskForgeDbContext readContext = new(DatabaseOptions);
        Job persisted = await readContext.Jobs.SingleAsync(candidate => candidate.Id == job.Id);
        Assert.Equal(JobStatus.DeadLettered, persisted.Status);
        Assert.Equal(0, persisted.RetryCount);
        Assert.Null(persisted.ResultJson);
        JobAttempt attempt = await readContext.JobAttempts.SingleAsync(candidate => candidate.JobId == job.Id);
        Assert.Equal(JobAttemptOutcome.PermanentlyFailed, attempt.Outcome);
        Assert.Equal("NonRetryableJobException", attempt.ErrorCode);
    }

    [Fact]
    public async Task Submit_job_returns_created_location_and_serialized_job()
    {
        await using WebApplicationFactory<Program> factory = CreateFactory();
        using HttpClient client = factory.CreateClient();

        using HttpResponseMessage response = await client.PostAsJsonAsync("/api/jobs", ValidJob());

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        JsonElement job = await response.Content.ReadFromJsonAsync<JsonElement>();
        Guid id = job.GetProperty("id").GetGuid();
        Assert.NotEqual(Guid.Empty, id);
        Assert.Equal($"/api/jobs/{id}", response.Headers.Location?.OriginalString);
        Assert.Equal("http-request", job.GetProperty("type").GetString());
        Assert.Equal("test-app", job.GetProperty("applicationId").GetString());
        Assert.Equal("High", job.GetProperty("priority").GetString());
        Assert.Equal("Queued", job.GetProperty("status").GetString());
        Assert.Equal("http://localhost/contract-test", job.GetProperty("payload").GetProperty("url").GetString());
        Assert.Equal(2, job.GetProperty("maxRetries").GetInt32());
        Assert.Equal(15, job.GetProperty("timeoutSeconds").GetInt32());
        Assert.NotEqual(default, job.GetProperty("createdAtUtc").GetDateTimeOffset());

        using HttpResponseMessage fetched = await client.GetAsync(response.Headers.Location);
        Assert.Equal(HttpStatusCode.OK, fetched.StatusCode);
        JsonElement fetchedJob = await fetched.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(id, fetchedJob.GetProperty("id").GetGuid());
    }

    [Fact]
    public async Task Invalid_requests_return_bad_request_for_validation_and_binding_errors()
    {
        await using WebApplicationFactory<Program> factory = CreateFactory();
        using HttpClient client = factory.CreateClient();

        using HttpResponseMessage invalid = await client.PostAsJsonAsync("/api/jobs", new { payload = new { url = "http://localhost/contract-test" } });

        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        Assert.Equal("application/problem+json", invalid.Content.Headers.ContentType?.MediaType);
        JsonElement problem = await invalid.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(400, problem.GetProperty("status").GetInt32());
        Assert.NotEmpty(problem.GetProperty("errors").GetProperty("Type").EnumerateArray());

        using StringContent malformedJson = new("{", Encoding.UTF8, "application/json");
        using HttpResponseMessage malformed = await client.PostAsync("/api/jobs", malformedJson);
        Assert.Equal(HttpStatusCode.BadRequest, malformed.StatusCode);
    }

    [Fact]
    public async Task Get_missing_job_returns_not_found()
    {
        await using WebApplicationFactory<Program> factory = CreateFactory();
        using HttpClient client = factory.CreateClient();
        Guid id = Guid.NewGuid();

        using HttpResponseMessage response = await client.GetAsync($"/api/jobs/{id}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        JsonElement body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal($"Job '{id}' was not found.", body.GetProperty("message").GetString());
    }

    [Fact]
    public async Task Idempotency_header_returns_ok_for_replay_and_conflict_for_changed_request()
    {
        await using WebApplicationFactory<Program> factory = CreateFactory();
        using HttpClient client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("Idempotency-Key", "contract-test-key");

        using HttpResponseMessage created = await client.PostAsJsonAsync("/api/jobs", ValidJob());
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        JsonElement original = await created.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("contract-test-key", original.GetProperty("idempotencyKey").GetString());

        using HttpResponseMessage replay = await client.PostAsJsonAsync("/api/jobs", ValidJob());
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        JsonElement replayed = await replay.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(original.GetProperty("id").GetGuid(), replayed.GetProperty("id").GetGuid());

        using HttpResponseMessage conflict = await client.PostAsJsonAsync("/api/jobs", ValidJob("Low"));
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        JsonElement conflictBody = await conflict.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("contract-test-key", conflictBody.GetProperty("idempotencyKey").GetString());
        Assert.False(string.IsNullOrWhiteSpace(conflictBody.GetProperty("message").GetString()));
    }

    [Fact]
    public async Task Get_stats_returns_counts_and_nullable_success_rate()
    {
        await using WebApplicationFactory<Program> factory = CreateFactory();
        using HttpClient client = factory.CreateClient();
        using HttpResponseMessage submitted = await client.PostAsJsonAsync("/api/jobs", ValidJob());
        Assert.Equal(HttpStatusCode.Created, submitted.StatusCode);

        using HttpResponseMessage response = await client.GetAsync("/api/stats");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        JsonElement stats = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, stats.GetProperty("totalJobs").GetInt64());
        Assert.Equal(JsonValueKind.Null, stats.GetProperty("successRatePercent").ValueKind);
        JsonElement counts = stats.GetProperty("countsByStatus");
        Assert.Equal(1, counts.GetProperty("queued").GetInt64());
        foreach (string status in new[] { "pending", "processing", "retrying", "completed", "deadLettered", "cancelled" })
        {
            Assert.Equal(0, counts.GetProperty(status).GetInt64());
        }
    }

    [Fact]
    public async Task Cancel_queued_job_returns_cancelled_job()
    {
        await using WebApplicationFactory<Program> factory = CreateFactory();
        using HttpClient client = factory.CreateClient();
        using HttpResponseMessage submitted = await client.PostAsJsonAsync("/api/jobs", ValidJob());
        Assert.Equal(HttpStatusCode.Created, submitted.StatusCode);
        JsonElement job = await submitted.Content.ReadFromJsonAsync<JsonElement>();
        Guid id = job.GetProperty("id").GetGuid();

        using HttpResponseMessage response = await client.PostAsync($"/api/jobs/{id}/cancel", null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        JsonElement cancelled = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(id, cancelled.GetProperty("id").GetGuid());
        Assert.Equal("Cancelled", cancelled.GetProperty("status").GetString());
        Assert.True(cancelled.GetProperty("cancellationRequested").GetBoolean());
    }

    [Fact]
    public async Task Readiness_returns_ok_when_database_is_available()
    {
        await using WebApplicationFactory<Program> factory = CreateFactory();
        using HttpClient client = factory.CreateClient();

        using HttpResponseMessage response = await client.GetAsync("/api/ready");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("{\"status\":\"Ready\"}", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Readiness_returns_service_unavailable_without_details_while_liveness_stays_healthy()
    {
        await using WebApplicationFactory<Program> factory = CreateFactory().WithWebHostBuilder(builder =>
            builder.ConfigureServices(services => services.RemoveAll<IHostedService>()));
        using HttpClient client = factory.CreateClient();
        await using TaskForgeDbContext context = new(DatabaseOptions);
        await context.Database.EnsureDeletedAsync();

        using HttpResponseMessage readiness = await client.GetAsync("/api/ready");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, readiness.StatusCode);
        Assert.Equal("application/json", readiness.Content.Headers.ContentType?.MediaType);
        Assert.Equal("{\"status\":\"Unavailable\"}", await readiness.Content.ReadAsStringAsync());

        using HttpResponseMessage liveness = await client.GetAsync("/api/health");
        Assert.Equal(HttpStatusCode.OK, liveness.StatusCode);
        JsonElement health = await liveness.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Healthy", health.GetProperty("status").GetString());
        Assert.Equal("TaskForge.Api", health.GetProperty("service").GetString());
        Assert.NotEqual(default, health.GetProperty("timestampUtc").GetDateTimeOffset());
    }

    [Fact]
    public async Task Get_attempts_returns_not_found_for_missing_job_and_empty_array_for_unexecuted_job()
    {
        await using WebApplicationFactory<Program> factory = CreateFactory();
        using HttpClient client = factory.CreateClient();
        Guid missingId = Guid.NewGuid();

        using HttpResponseMessage missing = await client.GetAsync($"/api/jobs/{missingId}/attempts");

        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        JsonElement error = await missing.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal($"Job '{missingId}' was not found.", error.GetProperty("message").GetString());
        using HttpResponseMessage submission = await client.PostAsJsonAsync("/api/jobs", ValidJob());
        JsonElement job = await submission.Content.ReadFromJsonAsync<JsonElement>();
        Guid id = job.GetProperty("id").GetGuid();
        using HttpResponseMessage response = await client.GetAsync($"/api/jobs/{id}/attempts");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("[]", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Get_attempts_returns_ordered_public_fields_for_only_the_requested_job()
    {
        await using WebApplicationFactory<Program> factory = CreateFactory();
        using HttpClient client = factory.CreateClient();
        using HttpResponseMessage submission = await client.PostAsJsonAsync("/api/jobs", ValidJob());
        Guid jobId = (await submission.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        DateTimeOffset start = new(2026, 9, 17, 12, 0, 0, TimeSpan.Zero);
        JobAttempt first = new(Guid.NewGuid(), jobId, 1, "worker-01", start);
        first.Finish(JobAttemptOutcome.Failed, start.AddMilliseconds(750), "RequestFailure", "The request failed.");
        JobAttempt second = new(Guid.NewGuid(), jobId, 2, "worker-02", start.AddSeconds(5));
        await using (TaskForgeDbContext context = new(DatabaseOptions))
        {
            Job unrelated = new(Guid.NewGuid(), "test-app", "example", "{}", JobPriority.Normal, 0, 5, start);
            context.Jobs.Add(unrelated);
            context.JobAttempts.AddRange(second, first, new JobAttempt(Guid.NewGuid(), unrelated.Id, 1, "other-worker", start));
            await context.SaveChangesAsync();
        }

        using HttpResponseMessage response = await client.GetAsync($"/api/jobs/{jobId}/attempts");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        JsonElement[] attempts = (await response.Content.ReadFromJsonAsync<JsonElement>()).EnumerateArray().ToArray();
        Assert.Equal([1, 2], attempts.Select(attempt => attempt.GetProperty("attemptNumber").GetInt32()));
        Assert.All(attempts, attempt => Assert.Equal(jobId, attempt.GetProperty("jobId").GetGuid()));
        Assert.Equal("Failed", attempts[0].GetProperty("outcome").GetString());
        Assert.Equal("worker-01", attempts[0].GetProperty("workerId").GetString());
        Assert.Equal(start, attempts[0].GetProperty("startedAtUtc").GetDateTimeOffset());
        Assert.Equal(start.AddMilliseconds(750), attempts[0].GetProperty("finishedAtUtc").GetDateTimeOffset());
        Assert.Equal(750, attempts[0].GetProperty("durationMilliseconds").GetInt64());
        Assert.Equal("RequestFailure", attempts[0].GetProperty("errorCode").GetString());
        Assert.Equal("The request failed.", attempts[0].GetProperty("errorMessage").GetString());
        Assert.Equal("Running", attempts[1].GetProperty("outcome").GetString());
        Assert.Equal(JsonValueKind.Null, attempts[1].GetProperty("finishedAtUtc").ValueKind);
        Assert.Equal(JsonValueKind.Null, attempts[1].GetProperty("durationMilliseconds").ValueKind);
        Assert.Equal(JsonValueKind.Null, attempts[1].GetProperty("errorMessage").ValueKind);
        Assert.Equal(9, attempts[0].EnumerateObject().Count());
        Assert.False(attempts[0].TryGetProperty("id", out _));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("a/project")]
    [InlineData("a project")]
    [InlineData("\u00e4-project")]
    public async Task Invalid_application_id_returns_validation_problem_without_persistence(string? applicationId)
    {
        await using WebApplicationFactory<Program> factory = CreateFactory();
        using HttpClient client = factory.CreateClient();
        using HttpResponseMessage response = await client.PostAsJsonAsync("/api/jobs", ValidJob(applicationId: applicationId!));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        JsonElement body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.NotEmpty(body.GetProperty("errors").GetProperty("ApplicationId").EnumerateArray());
        await using TaskForgeDbContext context = new(DatabaseOptions);
        Assert.Empty(await context.Jobs.ToListAsync());
    }

    [Fact]
    public async Task Missing_application_id_is_rejected_and_submission_defaults_are_preserved()
    {
        await using WebApplicationFactory<Program> factory = CreateFactory();
        using HttpClient client = factory.CreateClient();
        object payload = new { url = "http://localhost/defaults", method = "GET" };
        using HttpResponseMessage missing = await client.PostAsJsonAsync("/api/jobs", new { type = "http-request", payload });

        Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);
        JsonElement problem = await missing.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(problem.GetProperty("errors").TryGetProperty("ApplicationId", out _));
        using HttpResponseMessage accepted = await client.PostAsJsonAsync("/api/jobs", new { applicationId = " A-Project ", type = "http-request", payload });
        Assert.Equal(HttpStatusCode.Created, accepted.StatusCode);
        JsonElement job = await accepted.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("a-project", job.GetProperty("applicationId").GetString());
        Assert.Equal("Normal", job.GetProperty("priority").GetString());
        Assert.Equal(3, job.GetProperty("maxRetries").GetInt32());
        Assert.Equal(30, job.GetProperty("timeoutSeconds").GetInt32());
        await using TaskForgeDbContext context = new(DatabaseOptions);
        Assert.Single(await context.Jobs.ToListAsync());
    }

    [Fact]
    public async Task Application_id_length_is_validated_at_http_boundary()
    {
        await using WebApplicationFactory<Program> factory = CreateFactory();
        using HttpClient client = factory.CreateClient();
        string maximum = new('A', 100);
        using HttpResponseMessage accepted = await client.PostAsJsonAsync("/api/jobs", ValidJob(applicationId: $" {maximum} "));
        using HttpResponseMessage rejected = await client.PostAsJsonAsync("/api/jobs", ValidJob(applicationId: maximum + "a"));

        Assert.Equal(HttpStatusCode.Created, accepted.StatusCode);
        Assert.Equal(maximum.ToLowerInvariant(), (await accepted.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("applicationId").GetString());
        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        Assert.True((await rejected.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("errors").TryGetProperty("ApplicationId", out _));
        await using TaskForgeDbContext context = new(DatabaseOptions);
        Assert.Single(await context.Jobs.ToListAsync());
    }

    [Fact]
    public async Task Applications_have_independent_idempotency_and_normalized_list_filters()
    {
        await using WebApplicationFactory<Program> factory = CreateFactory();
        using HttpClient client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("Idempotency-Key", "shared-key");
        using HttpResponseMessage first = await client.PostAsJsonAsync("/api/jobs", ValidJob(applicationId: " A-Project "));
        using HttpResponseMessage second = await client.PostAsJsonAsync("/api/jobs", ValidJob(applicationId: "b-project"));
        using HttpResponseMessage duplicate = await client.PostAsJsonAsync("/api/jobs", ValidJob(applicationId: "A-PROJECT"));
        using HttpResponseMessage conflict = await client.PostAsJsonAsync("/api/jobs", ValidJob("Low", "a-project"));

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.Created, second.StatusCode);
        Assert.Equal(HttpStatusCode.OK, duplicate.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        Guid firstId = (await first.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        Guid secondId = (await second.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        Assert.NotEqual(firstId, secondId);
        Assert.Equal(firstId, (await duplicate.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid());

        using HttpResponseMessage filtered = await client.GetAsync("/api/jobs?applicationId=%20A-PROJECT%20&status=Queued&priority=High&pageSize=1");
        Assert.Equal(HttpStatusCode.OK, filtered.StatusCode);
        JsonElement page = await filtered.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, page.GetProperty("totalCount").GetInt32());
        Assert.Equal(firstId, Assert.Single(page.GetProperty("items").EnumerateArray()).GetProperty("id").GetGuid());
        using HttpResponseMessage all = await client.GetAsync("/api/jobs");
        Assert.Equal(2, (await all.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("totalCount").GetInt32());
        using HttpResponseMessage invalidFilter = await client.GetAsync("/api/jobs?applicationId=a%2Fb");
        Assert.Equal(HttpStatusCode.BadRequest, invalidFilter.StatusCode);
    }

    private WebApplicationFactory<Program> CreateFactory() => new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
    {
        builder.UseSetting("ConnectionStrings:TaskForge", ConnectionString);
        builder.UseSetting("Worker:Count", "0");
        builder.UseSetting("HttpRequestJobs:AllowedHosts:0", "localhost");
    });

    private static object ValidJob(string priority = "High", string applicationId = "test-app") => new
    {
        applicationId,
        type = "http-request",
        payload = new { url = "http://localhost/contract-test", method = "GET" },
        priority,
        maxRetries = 2,
        timeoutSeconds = 15
    };
}
