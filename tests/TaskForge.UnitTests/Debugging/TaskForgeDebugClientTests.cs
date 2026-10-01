using System.Net;
using System.Text;

using TaskForge.Debugging.Api;

namespace TaskForge.UnitTests.Debugging;

public sealed class TaskForgeDebugClientTests
{
    [Fact]
    public async Task GetHealthAsync_ReadsServerIdentityAndTime()
    {
        using HttpClient httpClient = CreateClient("/api/health", """
            {"status":"Healthy","service":"TaskForge.Api","timestampUtc":"2026-09-30T12:00:00Z"}
            """);

        HealthResponse health = await new TaskForgeDebugClient(httpClient).GetHealthAsync(CancellationToken.None);

        Assert.Equal("Healthy", health.Status);
        Assert.Equal("TaskForge.Api", health.Service);
        Assert.Equal(new DateTimeOffset(2026, 9, 30, 12, 0, 0, TimeSpan.Zero), health.TimestampUtc);
    }

    [Fact]
    public async Task GetStatisticsAsync_ReadsAllStatusCountsAndServerSuccessRate()
    {
        using HttpClient httpClient = CreateClient("/api/stats", """
            {
              "totalJobs": 3000000018,
              "countsByStatus": {
                "pending": 3000000000, "queued": 1, "processing": 2, "retrying": 3,
                "completed": 6, "deadLettered": 2, "cancelled": 4
              },
              "successRatePercent": 75
            }
            """);

        JobStatisticsResponse statistics = await new TaskForgeDebugClient(httpClient).GetStatisticsAsync(CancellationToken.None);

        Assert.Equal(3000000018, statistics.TotalJobs);
        Assert.Equal(new JobStatusCounts(3000000000, 1, 2, 3, 6, 2, 4), statistics.CountsByStatus);
        Assert.Equal(75m, statistics.SuccessRatePercent);
    }

    [Fact]
    public async Task GetStatisticsAsync_PreservesUndefinedSuccessRateForEmptyDatabase()
    {
        using HttpClient httpClient = CreateClient("/api/stats", """
            {
              "totalJobs": 0,
              "countsByStatus": {
                "pending": 0, "queued": 0, "processing": 0, "retrying": 0,
                "completed": 0, "deadLettered": 0, "cancelled": 0
              },
              "successRatePercent": null
            }
            """);

        JobStatisticsResponse statistics = await new TaskForgeDebugClient(httpClient).GetStatisticsAsync(CancellationToken.None);

        Assert.Equal(0, statistics.TotalJobs);
        Assert.Null(statistics.SuccessRatePercent);
    }

    [Fact]
    public async Task GetJobsAsync_SendsApplicationFilterAndReadsStringEnums()
    {
        using HttpClient httpClient = CreateClient("/api/jobs?page=1&pageSize=20&status=Retrying&applicationId=billing-api", """
            {
              "items": [{
                "id": "ced08076-32bc-4719-b990-e3880eb19877", "applicationId": "billing-api",
                "type": "send-email", "priority": "High", "status": "Retrying", "maxRetries": 3, "retryCount": 1,
                "createdAtUtc": "2026-09-30T12:00:00Z", "updatedAtUtc": "2026-09-30T12:01:00Z"
              }],
              "page": 1, "pageSize": 20, "totalCount": 1, "totalPages": 1
            }
            """);
        JobFilters filters = new("Retrying", null, null, "billing-api");

        JobPageResponse page = await new TaskForgeDebugClient(httpClient).GetJobsAsync(filters, 20, CancellationToken.None);

        JobSummary job = Assert.Single(page.Items);
        Assert.Equal("billing-api", job.ApplicationId);
        Assert.Equal("High", job.Priority);
        Assert.Equal("Retrying", job.Status);
        Assert.Equal(1, job.RetryCount);
        Assert.Equal(1, page.TotalCount);
    }

    [Theory]
    [InlineData("{\"title\":\"Database unavailable\",\"status\":503}", "Database unavailable")]
    [InlineData("Service unavailable", "Service unavailable")]
    public async Task GetStatisticsAsync_PreservesApiFailureStatusAndMessage(string body, string expectedMessage)
    {
        using HttpClient httpClient = CreateClient("/api/stats", body, HttpStatusCode.ServiceUnavailable);

        TaskForgeApiException exception = await Assert.ThrowsAsync<TaskForgeApiException>(
            () => new TaskForgeDebugClient(httpClient).GetStatisticsAsync(CancellationToken.None));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, exception.StatusCode);
        Assert.Equal(expectedMessage, exception.Message);
    }

    private static HttpClient CreateClient(string expectedPath, string body, HttpStatusCode statusCode = HttpStatusCode.OK)
    {
        return new HttpClient(new ResponseHandler(expectedPath, body, statusCode)) { BaseAddress = new Uri("http://localhost/") };
    }

    private sealed class ResponseHandler(string expectedPath, string body, HttpStatusCode statusCode) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal(expectedPath, request.RequestUri!.PathAndQuery);
            return Task.FromResult(new HttpResponseMessage(statusCode) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
    }
}
