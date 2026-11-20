using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;

using TaskForge.Domain.Jobs;
using TaskForge.Infrastructure.Persistence;
using TaskForge.SDK;
using TaskForge.SDK.Jobs;

namespace TaskForge.IntegrationTests.Api;

[Collection("SQL Server")]
public sealed class ControlledPayloadTests(SqlServerFixture fixture) : SqlServerTest(fixture)
{
    private const string ReportUrl = "https://wowiki.example/api/internal/taskforge/user-reports/42/ready";
    private readonly string _wowWikiKey = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    private readonly string _calculatorKey = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));

    [Fact]
    public async Task Protected_routes_reject_requests_without_a_configured_application_identity()
    {
        await using WebApplicationFactory<Program> server = CreateServer();
        using HttpClient client = server.CreateClient();
        string job = $"/api/jobs/{Guid.NewGuid()}";
        string attempt = $"{job}/attempts/{Guid.NewGuid()}";
        (HttpMethod Method, string Path)[] routes =
        [
            (HttpMethod.Post, "/api/jobs"), (HttpMethod.Get, "/api/jobs"), (HttpMethod.Get, "/api/stats"),
            (HttpMethod.Get, job), (HttpMethod.Get, $"{job}/attempts"),
            (HttpMethod.Post, $"{job}/cancel"), (HttpMethod.Post, $"{job}/retry"),
            (HttpMethod.Post, "/api/executions/wait"),
            (HttpMethod.Post, $"{attempt}/complete"), (HttpMethod.Post, $"{attempt}/fail")
        ];

        foreach (string? key in new[] { null, Convert.ToHexString(RandomNumberGenerator.GetBytes(32)) })
        {
            foreach ((HttpMethod method, string path) in routes)
            {
                using HttpRequestMessage request = new(method, path);
                if (key is not null)
                {
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
                }

                using HttpResponseMessage response = await client.SendAsync(request);
                Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
                Assert.Equal("Bearer", Assert.Single(response.Headers.WwwAuthenticate).Scheme);
            }
        }

        using HttpResponseMessage health = await client.GetAsync("/api/health");
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
    }

    [Fact]
    public async Task Only_registered_contracts_can_persist_payloads_and_nested_fields_are_checked()
    {
        await using WebApplicationFactory<Program> server = CreateServer();
        using HttpClient wiki = CreateClient(server, _wowWikiKey);
        using HttpClient calculator = CreateClient(server, _calculatorKey);
        object[] rejected =
        [
            new { url = ReportUrl, method = "POST", body = new { reportId = "42" }, headers = SourceHeader() },
            new { url = ReportUrl, method = "POST", body = new { reportId = "42", externalReference = "WOW-42", description = "extra" }, headers = SourceHeader() },
            new { url = ReportUrl, method = "POST", body = new { reportId = "42", externalReference = "WOW-42" }, headers = new Dictionary<string, string>(SourceHeader()) { ["Authorization"] = "example-value" } },
            new { url = "https://unexpected.example/api/internal/taskforge/user-reports/42/ready", method = "POST", body = new { reportId = "42", externalReference = "WOW-42" }, headers = SourceHeader() }
        ];

        foreach (object payload in rejected)
        {
            using HttpResponseMessage response = await wiki.PostAsJsonAsync("/api/jobs", new { applicationId = "wowiki", type = "http-request", payload });
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            JsonElement problem = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.True(problem.GetProperty("errors").TryGetProperty("Payload", out _));
        }

        foreach ((HttpClient client, object body) in new (HttpClient, object)[]
        {
            (wiki, new { applicationId = "wowiki", type = "unknown", payload = ReportPayload() }),
            (calculator, new { applicationId = "calculator", type = "calculate", payload = new { values = new[] { "42" } } }),
            (calculator, new { applicationId = "calculator", type = "calculate", payload = new { values = new[] { 42 }, note = new string('x', 17_000) } })
        })
        {
            using HttpResponseMessage response = await client.PostAsJsonAsync("/api/jobs", body);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }

        await using (TaskForgeDbContext read = new(DatabaseOptions))
        {
            Assert.Empty(await read.Jobs.ToListAsync());
        }

        using HttpResponseMessage valid = await wiki.PostAsJsonAsync("/api/jobs", new { applicationId = "wowiki", type = "http-request", payload = ReportPayload() });
        Assert.Equal(HttpStatusCode.Created, valid.StatusCode);
        using HttpResponseMessage calculation = await calculator.PostAsJsonAsync("/api/jobs", new { applicationId = "calculator", type = "calculate", payload = new { values = new[] { 12, 30 } } });
        Assert.Equal(HttpStatusCode.Created, calculation.StatusCode);
    }

    [Fact]
    public async Task An_authenticated_sdk_can_execute_but_another_application_cannot_read_claim_or_mutate_the_job()
    {
        await using WebApplicationFactory<Program> server = CreateServer();
        using HttpClient sdkHttp = server.CreateClient();
        using HttpClient wiki = CreateClient(server, _wowWikiKey);
        using HttpClient other = CreateClient(server, _calculatorKey);
        TaskForgeClient sdk = new(sdkHttp, new() { ApplicationId = "wowiki", BaseUrl = sdkHttp.BaseAddress!, ApiKey = _wowWikiKey });
        SubmitJobRequest request = new("http-request", JsonSerializer.SerializeToElement(ReportPayload()));
        JobResponse job = await sdk.SubmitAsync(request, "wowiki-report-42");
        Assert.Equal(job.Id, (await sdk.SubmitAsync(request, "wowiki-report-42")).Id);

        using HttpResponseMessage wrongSubmission = await other.PostAsJsonAsync("/api/jobs", new { applicationId = "wowiki", type = "http-request", payload = ReportPayload() });
        Assert.Equal(HttpStatusCode.Forbidden, wrongSubmission.StatusCode);
        using HttpResponseMessage wrongWait = await other.PostAsJsonAsync("/api/executions/wait", new { applicationId = "wowiki", workerId = "other", supportedTypes = new[] { "http-request" }, waitSeconds = 0 });
        Assert.Equal(HttpStatusCode.Forbidden, wrongWait.StatusCode);
        Assert.Equal(0, (await other.GetFromJsonAsync<JsonElement>("/api/jobs")).GetProperty("totalCount").GetInt32());
        Assert.Equal(0, (await other.GetFromJsonAsync<JsonElement>("/api/stats")).GetProperty("totalJobs").GetInt64());
        using HttpResponseMessage wrongFilter = await other.GetAsync("/api/jobs?applicationId=wowiki");
        Assert.Equal(HttpStatusCode.Forbidden, wrongFilter.StatusCode);
        foreach ((HttpMethod method, string path) in new (HttpMethod, string)[]
        {
            (HttpMethod.Get, ""), (HttpMethod.Get, "/attempts"), (HttpMethod.Post, "/cancel"), (HttpMethod.Post, "/retry")
        })
        {
            using HttpRequestMessage message = new(method, $"/api/jobs/{job.Id}{path}");
            using HttpResponseMessage response = await other.SendAsync(message);
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }

        ExecutionAssignmentResponse assignment = Assert.IsType<ExecutionAssignmentResponse>(await sdk.WaitAsync(new("worker", ["http-request"], 0)));
        Assert.Equal(job.Id, assignment.JobId);
        Assert.Equal("42", assignment.Payload.GetProperty("body").GetProperty("reportId").GetString());
        string completePath = $"/api/jobs/{job.Id}/attempts/{assignment.AttemptId}/complete";
        using HttpResponseMessage forged = await other.PostAsJsonAsync(completePath, new { applicationId = "wowiki", workerId = "worker", result = new { statusCode = 201 } });
        Assert.Equal(HttpStatusCode.Forbidden, forged.StatusCode);
        using HttpResponseMessage unrelated = await other.PostAsJsonAsync(completePath, new { applicationId = "calculator", workerId = "worker", result = new { statusCode = 201 } });
        Assert.Equal(HttpStatusCode.NotFound, unrelated.StatusCode);
        using HttpResponseMessage completed = await wiki.PostAsJsonAsync(completePath, new { applicationId = "wowiki", workerId = "worker", result = new { statusCode = 201 } });
        Assert.Equal(HttpStatusCode.OK, completed.StatusCode);
        Assert.Equal(TaskForge.SDK.Jobs.JobStatus.Completed, (await sdk.GetJobAsync(job.Id)).Status);
        Assert.Single(await sdk.GetAttemptsAsync(job.Id));
    }

    [Fact]
    public async Task Replay_revalidates_existing_payload_without_disrupting_a_scheduled_job()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        await using (TaskForgeDbContext database = new(DatabaseOptions))
        {
            Job legacy = new(Guid.NewGuid(), "calculator", "calculate", """{"values":[42],"oldField":true}""", TaskForge.Domain.Jobs.JobPriority.Normal, 3, 30, now);
            legacy.Queue(now);
            legacy.RequestCancellation(now);
            database.Jobs.Add(legacy);
            await database.SaveChangesAsync();
        }

        await using WebApplicationFactory<Program> server = CreateServer();
        using HttpClient calculator = CreateClient(server, _calculatorKey);
        JsonElement page = await calculator.GetFromJsonAsync<JsonElement>("/api/jobs");
        Guid legacyId = page.GetProperty("items")[0].GetProperty("id").GetGuid();
        using HttpResponseMessage invalidReplay = await calculator.PostAsync($"/api/jobs/{legacyId}/retry", null);
        Assert.Equal(HttpStatusCode.BadRequest, invalidReplay.StatusCode);
        using HttpResponseMessage scheduled = await calculator.PostAsJsonAsync("/api/jobs", new { applicationId = "calculator", type = "calculate", payload = new { values = new[] { 42 } }, scheduledAtUtc = now.AddHours(1) });
        Assert.Equal(HttpStatusCode.Created, scheduled.StatusCode);
        Guid scheduledId = (await scheduled.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        using HttpResponseMessage earlyWait = await calculator.PostAsJsonAsync("/api/executions/wait", new { applicationId = "calculator", workerId = "worker", supportedTypes = new[] { "calculate" }, waitSeconds = 0 });
        Assert.Equal(HttpStatusCode.NoContent, earlyWait.StatusCode);
        using HttpResponseMessage cancel = await calculator.PostAsync($"/api/jobs/{scheduledId}/cancel", null);
        Assert.Equal(HttpStatusCode.OK, cancel.StatusCode);
        using HttpResponseMessage replay = await calculator.PostAsync($"/api/jobs/{scheduledId}/retry", null);
        Assert.Equal(HttpStatusCode.Created, replay.StatusCode);
        Assert.Equal(JsonValueKind.Null, (await replay.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("scheduledAtUtc").ValueKind);
        await using TaskForgeDbContext read = new(DatabaseOptions);
        Assert.Equal(3, await read.Jobs.CountAsync());
    }

    private WebApplicationFactory<Program> CreateServer() => new WebApplicationFactory<Program>().WithWebHostBuilder(builder => builder
        .UseSetting("ConnectionStrings:TaskForge", ConnectionString)
        .UseSetting("JobAccess:Applications:wowiki:ApiKey", _wowWikiKey)
        .UseSetting("JobAccess:Applications:wowiki:Jobs:http-request:Contract", "wowiki-report")
        .UseSetting("JobAccess:Applications:wowiki:Jobs:http-request:Target", "https://wowiki.example")
        .UseSetting("JobAccess:Applications:calculator:ApiKey", _calculatorKey)
        .UseSetting("JobAccess:Applications:calculator:Jobs:calculate:Contract", "calculation"));

    private static HttpClient CreateClient(WebApplicationFactory<Program> server, string key)
    {
        HttpClient http = server.CreateClient();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);
        return http;
    }

    private static object ReportPayload() => new { url = ReportUrl, method = "POST", body = new { reportId = "42", externalReference = "WOW-42" }, headers = SourceHeader() };

    private static Dictionary<string, string> SourceHeader() => new() { ["X-TaskForge-Source"] = "wowiki-user-reports" };
}
