using System.Net.Http.Json;
using System.Text.Json;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace TaskForge.IntegrationTests.Api;

[Collection("SQL Server")]
public sealed class OpenApiContractTests(SqlServerFixture fixture) : SqlServerTest(fixture)
{
    [Fact]
    public async Task Server_operations_describe_success_error_and_history_schemas()
    {
        await using WebApplicationFactory<Program> factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            builder.UseSetting("ConnectionStrings:TaskForge", ConnectionString));
        using HttpClient client = factory.CreateClient();
        JsonElement document = await client.GetFromJsonAsync<JsonElement>("/openapi/v1.json");
        JsonElement paths = document.GetProperty("paths");
        AssertResponse(paths, "/api/health", "get", "200", "HealthResponse");
        AssertResponse(paths, "/api/ready", "get", "200", "ReadinessResponse");
        AssertResponse(paths, "/api/ready", "get", "503", "ReadinessResponse");
        AssertResponse(paths, "/api/stats", "get", "200", "JobStatistics");
        AssertResponse(paths, "/api/jobs", "post", "201", "JobResponse");
        AssertResponse(paths, "/api/jobs", "post", "200", "JobResponse");
        AssertResponse(paths, "/api/jobs", "post", "409", "IdempotencyConflictResponse");
        AssertResponse(paths, "/api/jobs", "get", "200", "JobPageResponse");
        AssertResponse(paths, "/api/jobs/{id}", "get", "200", "JobResponse");
        AssertResponse(paths, "/api/jobs/{id}", "get", "404", "ApiErrorResponse");
        AssertResponse(paths, "/api/jobs/{id}/attempts", "get", "404", "ApiErrorResponse");
        JsonElement history = paths.GetProperty("/api/jobs/{id}/attempts").GetProperty("get").GetProperty("responses")
            .GetProperty("200").GetProperty("content").GetProperty("application/json").GetProperty("schema");
        Assert.Equal("array", history.GetProperty("type").GetString());
        Assert.Equal("#/components/schemas/JobAttemptResponse", history.GetProperty("items").GetProperty("$ref").GetString());

        foreach (string operation in new[] { "retry", "cancel" })
        {
            string route = $"/api/jobs/{{id}}/{operation}";
            AssertResponse(paths, route, "post", operation == "retry" ? "201" : "200", "JobResponse");
            AssertResponse(paths, route, "post", "404", "ApiErrorResponse");
            AssertResponse(paths, route, "post", "409", "JobConflictResponse");
        }

        AssertResponse(paths, "/api/executions/wait", "post", "200", "ExecutionAssignmentResponse");
        Assert.False(paths.GetProperty("/api/executions/wait").GetProperty("post").GetProperty("responses").GetProperty("204").TryGetProperty("content", out _));
        foreach (string operation in new[] { "complete", "fail" })
        {
            string route = $"/api/jobs/{{jobId}}/attempts/{{attemptId}}/{operation}";
            AssertResponse(paths, route, "post", "200", "JobResponse");
            AssertResponse(paths, route, "post", "404", "ProblemDetails", "application/problem+json");
            AssertResponse(paths, route, "post", "409", "ProblemDetails", "application/problem+json");
        }

        foreach (string route in new[] { "/api/jobs", "/api/executions/wait", "/api/jobs/{jobId}/attempts/{attemptId}/complete", "/api/jobs/{jobId}/attempts/{attemptId}/fail" })
        {
            AssertResponse(paths, route, "post", "400", "HttpValidationProblemDetails", "application/problem+json");
            AssertResponse(paths, route, "post", "415", "ProblemDetails", "application/problem+json");
        }

        AssertResponse(paths, "/api/jobs", "get", "400", "HttpValidationProblemDetails", "application/problem+json");
        AssertResponse(paths, "/api/jobs/{id}/retry", "post", "400", "HttpValidationProblemDetails", "application/problem+json");
        AssertResponse(paths, "/api/workers", "get", "410", "ProblemDetails", "application/problem+json");
        AssertResponse(paths, "/api/workers/count", "put", "410", "ProblemDetails", "application/problem+json");
        JsonElement fields = document.GetProperty("components").GetProperty("schemas").GetProperty("JobAttemptResponse").GetProperty("properties");
        Assert.Equal(11, fields.EnumerateObject().Count());
        Assert.Equal("uuid", fields.GetProperty("attemptId").GetProperty("format").GetString());
        Assert.Equal("date-time", fields.GetProperty("deadlineAtUtc").GetProperty("format").GetString());
        foreach (string name in new[] { "JobStatus", "JobPriority", "JobAttemptOutcome" })
        {
            Assert.Equal("string", document.GetProperty("components").GetProperty("schemas").GetProperty(name).GetProperty("type").GetString());
        }

        foreach (JsonProperty path in paths.EnumerateObject().Where(path => path.Name != "/"))
        {
            foreach (JsonProperty operation in path.Value.EnumerateObject())
            {
                AssertResponse(paths, path.Name, operation.Name, "500", "ProblemDetails", "application/problem+json");
            }
        }
    }

    private static void AssertResponse(JsonElement paths, string route, string method, string status, string schema, string mediaType = "application/json")
    {
        JsonElement response = paths.GetProperty(route).GetProperty(method).GetProperty("responses").GetProperty(status);
        Assert.Equal($"#/components/schemas/{schema}", response.GetProperty("content").GetProperty(mediaType).GetProperty("schema").GetProperty("$ref").GetString());
    }
}
