using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace TaskForge.IntegrationTests.Api;

[Collection("SQL Server")]
public sealed class ApiContractTests(SqlServerFixture fixture) : SqlServerTest(fixture)
{
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

    private WebApplicationFactory<Program> CreateFactory() => new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
    {
        builder.UseSetting("ConnectionStrings:TaskForge", ConnectionString);
        builder.UseSetting("Worker:Count", "0");
        builder.UseSetting("HttpRequestJobs:AllowedHosts:0", "localhost");
    });

    private static object ValidJob(string priority = "High") => new
    {
        type = "http-request",
        payload = new { url = "http://localhost/contract-test", method = "GET" },
        priority,
        maxRetries = 2,
        timeoutSeconds = 15
    };
}
