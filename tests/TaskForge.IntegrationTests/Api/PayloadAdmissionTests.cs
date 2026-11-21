using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
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
public sealed class PayloadAdmissionTests(SqlServerFixture fixture) : SqlServerTest(fixture)
{
    private readonly string _referenceKey = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
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
    public async Task Invalid_json_payloads_are_rejected_without_persistence()
    {
        await using WebApplicationFactory<Program> server = CreateServer();
        using HttpClient reference = CreateClient(server, _referenceKey);
        string[] rejected =
        [
            """{"applicationId":"reference-client"}""",
            """{"applicationId":"reference-client","payload":[{"nested":{"key":1,"\u006bey":2}}]}""",
            $$"""{"applicationId":"reference-client","payload":"{{new string('x', 17_000)}}"}"""
        ];

        foreach (string body in rejected)
        {
            using HttpResponseMessage response = await SubmitJsonAsync(reference, body);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            JsonElement problem = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.True(problem.GetProperty("errors").TryGetProperty("Payload", out _));
        }

        string deepPayload = new string('[', 65) + "null" + new string(']', 65);
        using HttpResponseMessage tooDeep = await SubmitJsonAsync(reference, $$"""{"applicationId":"reference-client","payload":{{deepPayload}}}""");
        Assert.Equal(HttpStatusCode.BadRequest, tooDeep.StatusCode);
        using HttpResponseMessage malformed = await SubmitJsonAsync(reference, """{"applicationId":"reference-client","payload": [}""");
        Assert.Equal(HttpStatusCode.BadRequest, malformed.StatusCode);
        await using TaskForgeDbContext read = new(DatabaseOptions);
        Assert.Empty(await read.Jobs.ToListAsync());
    }

    [Fact]
    public async Task Key_only_application_can_submit_and_acquire_arbitrary_json_with_default_and_custom_types()
    {
        await using WebApplicationFactory<Program> server = CreateServer();
        using HttpClient reference = CreateClient(server, _referenceKey);
        TaskForgeClient sdk = new(reference, new() { ApplicationId = "reference-client", BaseUrl = reference.BaseAddress! });
        string[] payloads = ["""{"arbitrary": [1, true], "A":2, "a":3}""", """[{"nested":true}, null]""", "\"text\"", "1e5000", "false", "null"];
        for (int index = 0; index < payloads.Length; index++)
        {
            string type = index % 2 == 0 ? "general" : "custom-operation";
            string payload = payloads[index];
            string body = type == "general"
                ? $$"""{"applicationId":"reference-client","payload":{{payload}}}"""
                : $$"""{"applicationId":"reference-client","type":"{{type}}","payload":{{payload}}}""";
            using HttpResponseMessage submitted = await SubmitJsonAsync(reference, body);
            Assert.Equal(HttpStatusCode.Created, submitted.StatusCode);
            JsonElement job = await submitted.Content.ReadFromJsonAsync<JsonElement>();
            Guid id = job.GetProperty("id").GetGuid();
            Assert.Equal(type, job.GetProperty("type").GetString());
            ExecutionAssignmentResponse assignment = Assert.IsType<ExecutionAssignmentResponse>(await sdk.WaitAsync(new("worker", [type], 0)));
            Assert.Equal(id, assignment.JobId);
            Assert.Equal(type, assignment.Type);
            using JsonDocument original = JsonDocument.Parse(payload);
            Assert.True(JsonElement.DeepEquals(original.RootElement, assignment.Payload));
            await sdk.CompleteAsync(id, assignment.AttemptId, new("worker", null));

            await using TaskForgeDbContext read = new(DatabaseOptions);
            Assert.Equal(payload, await read.Jobs.Where(job => job.Id == id).Select(job => job.PayloadJson).SingleAsync());
        }
    }

    [Fact]
    public async Task Omitted_type_is_idempotent_with_explicit_general_for_the_same_payload_text()
    {
        await using WebApplicationFactory<Program> server = CreateServer();
        using HttpClient reference = CreateClient(server, _referenceKey);
        reference.DefaultRequestHeaders.Add("Idempotency-Key", "general-default");
        using HttpResponseMessage created = await SubmitJsonAsync(reference, """{"applicationId":"reference-client","payload":{"value": 1}}""");
        using HttpResponseMessage duplicate = await SubmitJsonAsync(reference, """{"applicationId":"reference-client","type":"general","payload":{"value": 1}}""");
        using HttpResponseMessage conflict = await SubmitJsonAsync(reference, """{"applicationId":"reference-client","type":"general","payload":{"value":1}}""");

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.Equal(HttpStatusCode.OK, duplicate.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        JsonElement first = await created.Content.ReadFromJsonAsync<JsonElement>();
        JsonElement second = await duplicate.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(first.GetProperty("id").GetGuid(), second.GetProperty("id").GetGuid());
        await using TaskForgeDbContext read = new(DatabaseOptions);
        Assert.Equal("""{"value": 1}""", (await read.Jobs.SingleAsync()).PayloadJson);
    }

    [Fact]
    public async Task An_authenticated_sdk_can_execute_but_another_application_cannot_read_claim_or_mutate_the_job()
    {
        await using WebApplicationFactory<Program> server = CreateServer();
        using HttpClient sdkHttp = server.CreateClient();
        using HttpClient reference = CreateClient(server, _referenceKey);
        using HttpClient other = CreateClient(server, _calculatorKey);
        TaskForgeClient sdk = new(sdkHttp, new() { ApplicationId = "reference-client", BaseUrl = sdkHttp.BaseAddress!, ApiKey = _referenceKey });
        SubmitJobRequest request = new("refresh-record", JsonSerializer.SerializeToElement(ReferencePayload()));
        JobResponse job = await sdk.SubmitAsync(request, "refresh-record-42");
        Assert.Equal(job.Id, (await sdk.SubmitAsync(request, "refresh-record-42")).Id);
        SubmitJobRequest differentInput = new("refresh-record", JsonSerializer.SerializeToElement(new { referenceId = "record-43" }));
        TaskForgeApiException conflict = await Assert.ThrowsAsync<TaskForgeApiException>(() => sdk.SubmitAsync(differentInput, "refresh-record-42"));
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);

        using HttpResponseMessage wrongSubmission = await other.PostAsJsonAsync("/api/jobs", new { applicationId = "reference-client", type = "refresh-record", payload = ReferencePayload() });
        Assert.Equal(HttpStatusCode.Forbidden, wrongSubmission.StatusCode);
        using HttpResponseMessage wrongWait = await other.PostAsJsonAsync("/api/executions/wait", new { applicationId = "reference-client", workerId = "other", supportedTypes = new[] { "refresh-record" }, waitSeconds = 0 });
        Assert.Equal(HttpStatusCode.Forbidden, wrongWait.StatusCode);
        Assert.Equal(0, (await other.GetFromJsonAsync<JsonElement>("/api/jobs")).GetProperty("totalCount").GetInt32());
        Assert.Equal(0, (await other.GetFromJsonAsync<JsonElement>("/api/stats")).GetProperty("totalJobs").GetInt64());
        using HttpResponseMessage wrongFilter = await other.GetAsync("/api/jobs?applicationId=reference-client");
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

        ExecutionAssignmentResponse assignment = Assert.IsType<ExecutionAssignmentResponse>(await sdk.WaitAsync(new("worker", ["refresh-record"], 0)));
        Assert.Equal(job.Id, assignment.JobId);
        Assert.Equal(job.Payload.GetRawText(), assignment.Payload.GetRawText());
        string completePath = $"/api/jobs/{job.Id}/attempts/{assignment.AttemptId}/complete";
        using HttpResponseMessage forged = await other.PostAsJsonAsync(completePath, new { applicationId = "reference-client", workerId = "worker", result = new { statusCode = 201 } });
        Assert.Equal(HttpStatusCode.Forbidden, forged.StatusCode);
        using HttpResponseMessage unrelated = await other.PostAsJsonAsync(completePath, new { applicationId = "calculator", workerId = "worker", result = new { statusCode = 201 } });
        Assert.Equal(HttpStatusCode.NotFound, unrelated.StatusCode);
        using HttpResponseMessage completed = await reference.PostAsJsonAsync(completePath, new { applicationId = "reference-client", workerId = "worker", result = new { statusCode = 201 } });
        Assert.Equal(HttpStatusCode.OK, completed.StatusCode);
        Assert.Equal(TaskForge.SDK.Jobs.JobStatus.Completed, (await sdk.GetJobAsync(job.Id)).Status);
        Assert.Single(await sdk.GetAttemptsAsync(job.Id));
    }

    [Fact]
    public async Task Replay_preserves_existing_payload_and_type_without_disrupting_a_scheduled_job()
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

        using HttpResponseMessage legacyReplay = await calculator.PostAsync($"/api/jobs/{legacyId}/retry", null);
        Assert.Equal(HttpStatusCode.Created, legacyReplay.StatusCode);
        JsonElement replayedLegacy = await legacyReplay.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("calculate", replayedLegacy.GetProperty("type").GetString());
        Assert.True(replayedLegacy.GetProperty("payload").GetProperty("oldField").GetBoolean());
        await using TaskForgeDbContext read = new(DatabaseOptions);
        Assert.Equal(4, await read.Jobs.CountAsync());
        Job original = await read.Jobs.SingleAsync(job => job.Id == legacyId);
        Assert.Equal(TaskForge.Domain.Jobs.JobStatus.Cancelled, original.Status);
        Assert.Equal("""{"values":[42],"oldField":true}""", original.PayloadJson);
    }

    private WebApplicationFactory<Program> CreateServer() => new WebApplicationFactory<Program>().WithWebHostBuilder(builder => builder
        .UseSetting("ConnectionStrings:TaskForge", ConnectionString)
        .UseSetting("JobAccess:Applications:reference-client:ApiKey", _referenceKey)
        .UseSetting("JobAccess:Applications:calculator:ApiKey", _calculatorKey));

    private static async Task<HttpResponseMessage> SubmitJsonAsync(HttpClient client, string body)
    {
        using StringContent content = new(body, Encoding.UTF8, "application/json");
        return await client.PostAsync("/api/jobs", content);
    }

    private static HttpClient CreateClient(WebApplicationFactory<Program> server, string key)
    {
        HttpClient http = server.CreateClient();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);
        return http;
    }

    private static object ReferencePayload() => new { referenceId = "record-42", options = new { force = false } };
}
