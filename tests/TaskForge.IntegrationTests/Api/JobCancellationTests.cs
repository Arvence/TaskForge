using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
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
public sealed class JobCancellationTests(SqlServerFixture fixture) : SqlServerTest(fixture)
{
    [Theory]
    [InlineData(JobStatus.Pending)]
    [InlineData(JobStatus.Queued)]
    [InlineData(JobStatus.Retrying)]
    [InlineData(JobStatus.Processing)]
    public async Task Cancel_persists_without_connected_client_and_duplicate_is_unchanged(JobStatus status)
    {
        Job job = await SeedAsync(status);
        await using WebApplicationFactory<Program> factory = CreateFactory();
        using HttpClient client = factory.CreateClient();

        using HttpResponseMessage response = await client.PostAsync($"/api/jobs/{job.Id}/cancel", null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        JsonElement body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Cancelled", body.GetProperty("status").GetString());
        Assert.True(body.GetProperty("cancellationRequested").GetBoolean());
        await using TaskForgeDbContext read = new(DatabaseOptions);
        Job persisted = await read.Jobs.AsNoTracking().SingleAsync();
        Assert.Equal(JobStatus.Cancelled, persisted.Status);
        Assert.Equal(job.RetryCount, persisted.RetryCount);
        Assert.Null(persisted.OwningWorkerId);
        Assert.Null(persisted.LeaseExpiresAtUtc);
        Assert.Null(persisted.NextRetryAtUtc);
        Assert.Null(persisted.ResultJson);
        Assert.Null(persisted.CompletedAtUtc);
        JobAttempt? attempt = await read.JobAttempts.AsNoTracking().SingleOrDefaultAsync();
        if (status == JobStatus.Processing)
        {
            Assert.NotNull(attempt);
            Assert.Equal(JobAttemptOutcome.Cancelled, attempt.Outcome);
            Assert.Equal("CancellationRequested", attempt.ErrorCode);
            Assert.Equal(persisted.UpdatedAtUtc, attempt.FinishedAtUtc);
            Assert.Equal((long)(attempt.FinishedAtUtc!.Value - attempt.StartedAtUtc).TotalMilliseconds, attempt.DurationMilliseconds);
        }
        else if (status == JobStatus.Retrying)
        {
            Assert.Equal(JobAttemptOutcome.Failed, attempt!.Outcome);
        }
        else
        {
            Assert.Null(attempt);
        }

        using HttpResponseMessage fetched = await client.GetAsync($"/api/jobs/{job.Id}");
        Assert.Equal("Cancelled", (await fetched.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("status").GetString());
        if (status == JobStatus.Processing)
        {
            using HttpResponseMessage history = await client.GetAsync($"/api/jobs/{job.Id}/attempts");
            Assert.Contains("Cancelled", await history.Content.ReadAsStringAsync());
        }

        string snapshot = await SnapshotAsync();
        using HttpResponseMessage duplicate = await client.PostAsync($"/api/jobs/{job.Id}/cancel", null);
        Assert.Equal(HttpStatusCode.OK, duplicate.StatusCode);
        Assert.Equal(await response.Content.ReadAsStringAsync(), await duplicate.Content.ReadAsStringAsync());
        Assert.Equal(snapshot, await SnapshotAsync());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public async Task Deadline_is_resolved_before_cancellation_even_during_lease_grace(int maxRetries)
    {
        Job job = await SeedAsync(JobStatus.Processing, expired: true, maxRetries: maxRetries);
        await using WebApplicationFactory<Program> factory = CreateFactory();
        using HttpClient client = factory.CreateClient();

        using HttpResponseMessage response = await client.PostAsync($"/api/jobs/{job.Id}/cancel", null);

        Assert.Equal(maxRetries == 0 ? HttpStatusCode.Conflict : HttpStatusCode.OK, response.StatusCode);
        await using TaskForgeDbContext read = new(DatabaseOptions);
        Job persisted = await read.Jobs.SingleAsync();
        JobAttempt attempt = await read.JobAttempts.SingleAsync();
        Assert.Equal(maxRetries == 0 ? JobStatus.DeadLettered : JobStatus.Cancelled, persisted.Status);
        Assert.Equal(maxRetries > 0, persisted.CancellationRequested);
        Assert.Equal(1, persisted.RetryCount);
        Assert.Equal(JobAttemptOutcome.TimedOut, attempt.Outcome);
        Assert.Equal("Timeout", attempt.ErrorCode);
        Assert.StartsWith("Timeout:", persisted.LastError);
        Assert.Equal(persisted.UpdatedAtUtc, attempt.FinishedAtUtc);
        Assert.Null(persisted.OwningWorkerId);
        Assert.Null(persisted.LeaseExpiresAtUtc);
        Assert.Null(persisted.NextRetryAtUtc);
        string snapshot = await SnapshotAsync();
        using HttpResponseMessage duplicate = await client.PostAsync($"/api/jobs/{job.Id}/cancel", null);
        Assert.Equal(response.StatusCode, duplicate.StatusCode);
        Assert.Equal(snapshot, await SnapshotAsync());
        Assert.Equal(ExecutionResult.TimedOut, await Store(read).TransitionAsync(Identity(job, attempt), new ExecutionReport.Complete("{}")));
        Assert.Equal(ExecutionResult.TimedOut, await Store(read).TransitionAsync(Identity(job, attempt), new ExecutionReport.Fail("Failure", "Late failure.")));
        Assert.Equal(snapshot, await SnapshotAsync());
    }

    [Theory]
    [InlineData(JobStatus.Completed)]
    [InlineData(JobStatus.DeadLettered)]
    public async Task Finished_jobs_are_unchanged_and_missing_job_returns_404(JobStatus status)
    {
        Job job = await SeedAsync(status);
        string snapshot = await SnapshotAsync();
        await using WebApplicationFactory<Program> factory = CreateFactory();
        using HttpClient client = factory.CreateClient();
        using HttpResponseMessage response = await client.PostAsync($"/api/jobs/{job.Id}/cancel", null);
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(status.ToString(), (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("status").GetString());
        using HttpResponseMessage missing = await client.PostAsync($"/api/jobs/{Guid.NewGuid()}/cancel", null);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Equal(snapshot, await SnapshotAsync());
    }

    [Fact]
    public async Task Later_complete_and_fail_reports_clearly_reject_cancelled_execution()
    {
        Job job = await SeedAsync(JobStatus.Processing);
        await using TaskForgeDbContext context = new(DatabaseOptions);
        JobAttempt attempt = await context.JobAttempts.SingleAsync();
        Assert.Equal(JobCancellationStatus.Accepted, (await new JobCancellationService(Store(context), new()).RequestAsync(job.Id)).Status);
        string snapshot = await SnapshotAsync();

        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddTaskForgeSqlServer(ConnectionString);
        builder.Services.AddOptions();
        builder.Services.AddSingleton<JobRetryPolicy>();
        builder.Services.AddTaskForgeExecutionReporting();
        await using WebApplication app = builder.Build();
        app.MapTaskForgeExecutionEndpoints();
        await app.StartAsync();
        using HttpClient client = app.GetTestClient();
        foreach (string operation in new[] { "complete", "fail", "complete", "fail" })
        {
            using HttpResponseMessage response = await client.PostAsJsonAsync($"/api/jobs/{job.Id}/attempts/{attempt.Id}/{operation}",
                new { applicationId = job.ApplicationId, workerId = attempt.WorkerId, result = new { late = true }, errorCode = "Failure", errorMessage = "Late failure." });
            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
            Assert.Contains("Cancelled", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("detail").GetString());
            Assert.Equal(snapshot, await SnapshotAsync());
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Cancellation_and_completion_serialize_with_consistent_job_and_attempt(bool cancellationFirst)
    {
        Job job = await SeedAsync(JobStatus.Processing);
        await using TaskForgeDbContext read = new(DatabaseOptions);
        JobAttempt attempt = await read.JobAttempts.SingleAsync();
        await using var transaction = await read.Database.BeginTransactionAsync();
        await read.Database.ExecuteSqlInterpolatedAsync($"SELECT [Id] FROM [Jobs] WITH (UPDLOCK, HOLDLOCK) WHERE [Id] = {job.Id}");
        await using TaskForgeDbContext competitor = new(DatabaseOptions);
        Task<JobCancellationResult>? cancellation = null;
        Task<ExecutionResult>? completion = null;
        if (cancellationFirst)
        {
            completion = Store(competitor).TransitionAsync(Identity(job, attempt), new ExecutionReport.Complete("{}"));
            job = await read.Jobs.SingleAsync();
            job.RequestCancellation(DateTimeOffset.UtcNow);
            job.Cancel(DateTimeOffset.UtcNow);
            attempt.Finish(JobAttemptOutcome.Cancelled, job.UpdatedAtUtc, "CancellationRequested", "Job cancellation was requested.");
        }
        else
        {
            cancellation = Store(competitor).CancelAsync(job.Id);
            job = await read.Jobs.SingleAsync();
            job.Complete("{}", DateTimeOffset.UtcNow);
            attempt.Finish(JobAttemptOutcome.Succeeded, job.UpdatedAtUtc);
        }

        await read.SaveChangesAsync();
        await transaction.CommitAsync();
        if (cancellationFirst)
        {
            Assert.Equal(ExecutionResult.Cancelled, await completion!);
        }
        else
        {
            Assert.Equal(JobCancellationStatus.AlreadyFinished, (await cancellation!).Status);
        }

        read.ChangeTracker.Clear();
        Assert.Equal(cancellationFirst ? JobStatus.Cancelled : JobStatus.Completed, (await read.Jobs.SingleAsync()).Status);
        Assert.Equal(cancellationFirst ? JobAttemptOutcome.Cancelled : JobAttemptOutcome.Succeeded, (await read.JobAttempts.SingleAsync()).Outcome);
    }

    [Fact]
    public async Task Concurrent_cancel_and_complete_have_only_one_winner()
    {
        Job job = await SeedAsync(JobStatus.Processing);
        await using TaskForgeDbContext cancelling = new(DatabaseOptions);
        await using TaskForgeDbContext completing = new(DatabaseOptions);
        JobAttempt attempt = await completing.JobAttempts.SingleAsync();
        Task<JobCancellationResult> cancellation = Store(cancelling).CancelAsync(job.Id);
        Task<ExecutionResult> completion = Store(completing).TransitionAsync(Identity(job, attempt), new ExecutionReport.Complete("{}"));
        await Task.WhenAll(cancellation, completion);
        JobCancellationResult cancellationResult = await cancellation;
        ExecutionResult completionResult = await completion;

        await using TaskForgeDbContext read = new(DatabaseOptions);
        Job persisted = await read.Jobs.SingleAsync();
        JobAttempt finished = await read.JobAttempts.SingleAsync();
        if (cancellationResult.Status == JobCancellationStatus.Accepted)
        {
            Assert.Equal(ExecutionResult.Cancelled, completionResult);
            Assert.Equal(JobStatus.Cancelled, persisted.Status);
            Assert.Equal(JobAttemptOutcome.Cancelled, finished.Outcome);
            Assert.Null(persisted.ResultJson);
            Assert.Null(persisted.CompletedAtUtc);
        }
        else
        {
            Assert.Equal(JobCancellationStatus.AlreadyFinished, cancellationResult.Status);
            Assert.Equal(ExecutionResult.Accepted, completionResult);
            Assert.Equal(JobStatus.Completed, persisted.Status);
            Assert.Equal(JobAttemptOutcome.Succeeded, finished.Outcome);
        }

        Assert.Null(persisted.OwningWorkerId);
        Assert.Null(persisted.LeaseExpiresAtUtc);
    }

    [Fact]
    public async Task Concurrent_cancellations_are_idempotent()
    {
        Job job = await SeedAsync(JobStatus.Processing);
        await using TaskForgeDbContext first = new(DatabaseOptions);
        await using TaskForgeDbContext second = new(DatabaseOptions);
        JobCancellationResult[] results = await Task.WhenAll(Store(first).CancelAsync(job.Id), Store(second).CancelAsync(job.Id));
        Assert.All(results, result => Assert.Equal(JobCancellationStatus.Accepted, result.Status));
        Assert.Equal(results[0].Job!.Version, results[1].Job!.Version);
        Assert.Equal(job.Version + 2, results[0].Job!.Version);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Persistence_failure_rolls_back_job_and_attempt_and_retry_succeeds(bool expired)
    {
        Job job = await SeedAsync(JobStatus.Processing, expired);
        string snapshot = await SnapshotAsync();
        DbContextOptions<TaskForgeDbContext> options = new DbContextOptionsBuilder<TaskForgeDbContext>(DatabaseOptions)
            .AddInterceptors(new RejectSavedChanges()).Options;
        await using TaskForgeDbContext failing = new(options);
        await Assert.ThrowsAsync<InjectedPersistenceException>(() => Store(failing).CancelAsync(job.Id));
        Assert.Equal(snapshot, await SnapshotAsync());
        await using TaskForgeDbContext retry = new(DatabaseOptions);
        Assert.Equal(JobCancellationStatus.Accepted, (await Store(retry).CancelAsync(job.Id)).Status);
        Assert.Equal(expired ? JobAttemptOutcome.TimedOut : JobAttemptOutcome.Cancelled, (await retry.JobAttempts.SingleAsync()).Outcome);
    }

    private async Task<Job> SeedAsync(JobStatus status, bool expired = false, int maxRetries = 2)
    {
        await using TaskForgeDbContext context = new(DatabaseOptions);
        DateTimeOffset now = await context.Database.SqlQuery<DateTimeOffset>($"SELECT TODATETIMEOFFSET(SYSUTCDATETIME(), '+00:00') AS [Value]").SingleAsync();
        DateTimeOffset start = expired ? now.AddSeconds(-301) : now;
        Job job = new(Guid.NewGuid(), "test-app", "remote-only", "{}", JobPriority.Normal, maxRetries, 300, start);
        if (status != JobStatus.Pending)
        {
            job.Queue(start);
        }

        if (status is JobStatus.Processing or JobStatus.Retrying or JobStatus.Completed or JobStatus.DeadLettered)
        {
            job.StartProcessing("remote-worker", start.AddMinutes(10), start);
            JobAttempt attempt = new(Guid.NewGuid(), job.Id, 1, "remote-worker", start);
            if (status == JobStatus.Retrying)
            {
                job.Fail("Failure", now.AddMinutes(1), now);
                attempt.Finish(JobAttemptOutcome.Failed, now, "Failure", "Retry later.");
            }
            else if (status == JobStatus.Completed)
            {
                job.Complete("{}", now);
                attempt.Finish(JobAttemptOutcome.Succeeded, now);
            }
            else if (status == JobStatus.DeadLettered)
            {
                job.DeadLetter("Permanent failure", now);
                attempt.Finish(JobAttemptOutcome.PermanentlyFailed, now);
            }

            context.JobAttempts.Add(attempt);
        }

        context.Jobs.Add(job);
        await context.SaveChangesAsync();
        return job;
    }

    private WebApplicationFactory<Program> CreateFactory() => new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
    {
        builder.UseSetting("ConnectionStrings:TaskForge", ConnectionString);
        builder.UseSetting("Worker:Count", "0");
    });

    private static EfCoreExecutionStore Store(TaskForgeDbContext context) => new(context, new JobRetryPolicy(Options.Create(new WorkerOptions())));

    private static ExecutionIdentity Identity(Job job, JobAttempt attempt) => new(job.ApplicationId, job.Id, attempt.Id, attempt.WorkerId);

    private async Task<string> SnapshotAsync()
    {
        await using TaskForgeDbContext context = new(DatabaseOptions);
        return JsonSerializer.Serialize(new { Job = await context.Jobs.AsNoTracking().SingleAsync(), Attempts = await context.JobAttempts.AsNoTracking().ToArrayAsync() });
    }

    private sealed class InjectedPersistenceException : Exception;

    private sealed class RejectSavedChanges : SaveChangesInterceptor
    {
        public override ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result, CancellationToken cancellationToken = default) =>
            throw new InjectedPersistenceException();
    }
}
