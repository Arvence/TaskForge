using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;

using TaskForge.Api.Jobs;
using TaskForge.Domain.Jobs;
using TaskForge.Infrastructure.Persistence;

namespace TaskForge.IntegrationTests.Api;

[Collection("SQL Server")]
public sealed class JobReplayTests(SqlServerFixture fixture) : SqlServerTest(fixture)
{
    private static readonly DateTimeOffset CreatedAt = new(2026, 9, 17, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(JobStatus.DeadLettered)]
    [InlineData(JobStatus.Cancelled)]
    public async Task Replay_creates_an_independent_execution_and_preserves_source_history(JobStatus status)
    {
        Job source = CreateSource(status);
        JobAttempt first = new(Guid.NewGuid(), source.Id, 1, "original-worker", CreatedAt.AddSeconds(1));
        first.Finish(JobAttemptOutcome.Failed, CreatedAt.AddSeconds(2), "Failure", "First failure.");
        JobAttempt second = new(Guid.NewGuid(), source.Id, 2, "original-worker", CreatedAt.AddSeconds(3));
        second.Finish(status == JobStatus.Cancelled ? JobAttemptOutcome.Cancelled : JobAttemptOutcome.Failed,
            CreatedAt.AddSeconds(4), "Terminal", "Original terminal outcome.");
        await using (TaskForgeDbContext context = new(DatabaseOptions))
        {
            context.Jobs.Add(source);
            context.JobAttempts.AddRange(first, second);
            await context.SaveChangesAsync();
        }

        string sourceSnapshot = JsonSerializer.Serialize(source);
        string attemptsSnapshot = JsonSerializer.Serialize(new[] { first, second });
        await using WebApplicationFactory<Program> factory = CreateFactory();
        using HttpClient client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("Idempotency-Key", source.IdempotencyKey);

        using HttpResponseMessage response = await client.PostAsync($"/api/jobs/{source.Id}/retry", null);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        JsonElement body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Guid replayId = body.GetProperty("id").GetGuid();
        Assert.NotEqual(source.Id, replayId);
        Assert.NotEqual(Guid.Empty, replayId);
        Assert.Equal($"/api/jobs/{replayId}", response.Headers.Location?.OriginalString);
        Assert.Equal("Queued", body.GetProperty("status").GetString());
        Assert.Equal(source.ApplicationId, body.GetProperty("applicationId").GetString());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("idempotencyKey").ValueKind);

        await using (TaskForgeDbContext context = new(DatabaseOptions))
        {
            Job replay = await context.Jobs.AsNoTracking().SingleAsync(job => job.Id == replayId);
            Assert.Equal(source.Type, replay.Type);
            Assert.Equal(source.ApplicationId, replay.ApplicationId);
            Assert.Equal(source.PayloadJson, replay.PayloadJson);
            Assert.Equal(source.Priority, replay.Priority);
            Assert.Equal(source.MaxRetries, replay.MaxRetries);
            Assert.Equal(source.TimeoutSeconds, replay.TimeoutSeconds);
            Assert.Equal(0, replay.RetryCount);
            Assert.False(replay.CancellationRequested);
            Assert.True(replay.CreatedAtUtc > source.UpdatedAtUtc);
            Assert.Equal(replay.CreatedAtUtc, replay.QueuedAtUtc);
            Assert.Null(replay.StartedAtUtc);
            Assert.Null(replay.CompletedAtUtc);
            Assert.Null(replay.NextRetryAtUtc);
            Assert.Null(replay.OwningWorkerId);
            Assert.Null(replay.LeaseExpiresAtUtc);
            Assert.Null(replay.LastError);
            Assert.Null(replay.ResultJson);
            Assert.Empty(await context.JobAttempts.Where(attempt => attempt.JobId == replayId).ToListAsync());
        }

        using HttpResponseMessage acquired = await client.PostAsJsonAsync("/api/executions/wait",
            new { applicationId = "test-app", workerId = "replay-worker", supportedTypes = new[] { source.Type }, waitSeconds = 0 });
        Assert.Equal(HttpStatusCode.OK, acquired.StatusCode);
        ExecutionAssignmentResponse assignment = (await acquired.Content.ReadFromJsonAsync<ExecutionAssignmentResponse>())!;
        Assert.Equal(replayId, assignment.JobId);
        Assert.Equal(source.PayloadJson, assignment.Payload.GetRawText());
        using HttpResponseMessage completed = await client.PostAsJsonAsync($"/api/jobs/{replayId}/attempts/{assignment.AttemptId}/complete",
            new { applicationId = "test-app", workerId = "replay-worker", result = new { replayed = true } });
        Assert.Equal(HttpStatusCode.OK, completed.StatusCode);

        await using (TaskForgeDbContext context = new(DatabaseOptions))
        {
            Job replay = await context.Jobs.AsNoTracking().SingleAsync(job => job.Id == replayId);
            Assert.Equal(JobStatus.Completed, replay.Status);
            JobAttempt attempt = await context.JobAttempts.SingleAsync(candidate => candidate.JobId == replayId);
            Assert.Equal(1, attempt.AttemptNumber);
            Assert.Equal(JobAttemptOutcome.Succeeded, attempt.Outcome);
            Assert.Equal("replay-worker", attempt.WorkerId);
        }

        using HttpResponseMessage repeated = await client.PostAsync($"/api/jobs/{source.Id}/retry", null);
        Assert.Equal(HttpStatusCode.Created, repeated.StatusCode);
        Guid repeatedId = (await repeated.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        Assert.NotEqual(replayId, repeatedId);
        Assert.NotEqual(source.Id, repeatedId);
        await using TaskForgeDbContext readContext = new(DatabaseOptions);
        Assert.Equal(3, await readContext.Jobs.CountAsync());
        Assert.Equal(sourceSnapshot, JsonSerializer.Serialize(await readContext.Jobs.AsNoTracking().SingleAsync(job => job.Id == source.Id)));
        Assert.Equal(attemptsSnapshot, JsonSerializer.Serialize(await readContext.JobAttempts.AsNoTracking()
            .Where(attempt => attempt.JobId == source.Id).OrderBy(attempt => attempt.AttemptNumber).ToArrayAsync()));
    }

    [Theory]
    [InlineData(JobStatus.Pending)]
    [InlineData(JobStatus.Queued)]
    [InlineData(JobStatus.Processing)]
    [InlineData(JobStatus.Retrying)]
    [InlineData(JobStatus.Completed)]
    public async Task Replay_rejects_inappropriate_source_states_without_changes(JobStatus status)
    {
        Job source = CreateSource(status);
        await using TaskForgeDbContext context = new(DatabaseOptions);
        context.Jobs.Add(source);
        await context.SaveChangesAsync();
        string snapshot = JsonSerializer.Serialize(source);
        await using WebApplicationFactory<Program> factory = CreateFactory();
        using HttpClient client = factory.CreateClient();

        using HttpResponseMessage response = await client.PostAsync($"/api/jobs/{source.Id}/retry", null);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        JsonElement error = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(status.ToString(), error.GetProperty("status").GetString());
        Assert.Contains("Only DeadLettered or Cancelled jobs can be replayed.", error.GetProperty("message").GetString());
        Assert.Equal(snapshot, JsonSerializer.Serialize(await context.Jobs.AsNoTracking().SingleAsync()));
        Assert.Empty(await context.JobAttempts.ToListAsync());
    }

    [Fact]
    public async Task Replay_missing_source_returns_not_found()
    {
        await using WebApplicationFactory<Program> factory = CreateFactory();
        using HttpClient client = factory.CreateClient();
        Guid id = Guid.NewGuid();

        using HttpResponseMessage response = await client.PostAsync($"/api/jobs/{id}/retry", null);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        JsonElement error = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal($"Job '{id}' was not found.", error.GetProperty("message").GetString());
        await using TaskForgeDbContext context = new(DatabaseOptions);
        Assert.Empty(await context.Jobs.ToListAsync());
    }

    [Fact]
    public async Task Replay_accepts_types_without_server_handlers()
    {
        Job source = new(Guid.NewGuid(), "test-app", "removed-handler", "{}", JobPriority.Normal, 1, 15, CreatedAt);
        source.RequestCancellation(CreatedAt);
        await using TaskForgeDbContext context = new(DatabaseOptions);
        context.Jobs.Add(source);
        await context.SaveChangesAsync();
        string snapshot = JsonSerializer.Serialize(source);
        await using WebApplicationFactory<Program> factory = CreateFactory();
        using HttpClient client = factory.CreateClient();

        using HttpResponseMessage response = await client.PostAsync($"/api/jobs/{source.Id}/retry", null);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        JsonElement replay = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("removed-handler", replay.GetProperty("type").GetString());
        Assert.Equal("Queued", replay.GetProperty("status").GetString());
        Assert.Equal(snapshot, JsonSerializer.Serialize(await context.Jobs.AsNoTracking().SingleAsync(job => job.Id == source.Id)));
        Assert.Equal(2, await context.Jobs.CountAsync());
    }

    private WebApplicationFactory<Program> CreateFactory() => new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
    {
        builder.UseSetting("ConnectionStrings:TaskForge", ConnectionString);
    });

    private static Job CreateSource(JobStatus status)
    {
        Job job = new(Guid.NewGuid(), "test-app", "http-request", """{"url":"http://localhost/replay","method":"GET"}""", JobPriority.High, 1, 15, CreatedAt, "original-key");
        if (status == JobStatus.Pending)
        {
            return job;
        }

        job.Queue(CreatedAt);
        if (status == JobStatus.Queued)
        {
            return job;
        }

        DateTimeOffset startedAt = status == JobStatus.Processing ? DateTimeOffset.UtcNow : CreatedAt.AddSeconds(1);
        job.StartProcessing("original-worker", leaseExpiresAtUtc: startedAt.AddMinutes(1), now: startedAt);
        if (status == JobStatus.Processing)
        {
            return job;
        }

        if (status == JobStatus.Completed)
        {
            job.Complete("{}", CreatedAt.AddSeconds(2));
            return job;
        }

        job.Fail("First failure.", CreatedAt.AddSeconds(3), CreatedAt.AddSeconds(2));
        if (status == JobStatus.Retrying)
        {
            return job;
        }

        job.QueueRetry(CreatedAt.AddSeconds(3));
        job.StartProcessing("original-worker", leaseExpiresAtUtc: CreatedAt.AddMinutes(1), now: CreatedAt.AddSeconds(3));
        if (status == JobStatus.Cancelled)
        {
            job.RequestCancellation(CreatedAt.AddSeconds(4));
            job.Cancel(CreatedAt.AddSeconds(4));
        }
        else
        {
            job.Fail("Original terminal outcome.", CreatedAt.AddSeconds(5), CreatedAt.AddSeconds(4));
        }

        return job;
    }
}
