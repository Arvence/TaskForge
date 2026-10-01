using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

using TaskForge.SDK;
using TaskForge.SDK.Jobs;

namespace TaskForge.IntegrationTests.SDK;

[Collection("SQL Server")]
public sealed class TaskForgeClientContractTests(SqlServerFixture fixture) : SqlServerTest(fixture)
{
    [Fact]
    public async Task Sdk_submission_routing_completion_and_history_match_the_real_server_contract()
    {
        await using WebApplicationFactory<Program> factory = CreateFactory();
        using HttpClient http = factory.CreateClient();
        TaskForgeClient first = Client(http, " A-PROJECT ");
        TaskForgeClient second = Client(http, "b-project");
        JsonElement payload = JsonSerializer.SerializeToElement(new { values = new[] { 20, 22 }, note = "SDK contract", optional = (string?)null });
        SubmitJobRequest submission = new("sdk-calculate", payload, JobPriority.Critical, 1, 30);

        JobResponse a = await first.SubmitAsync(submission, "shared-key");
        JobResponse b = await second.SubmitAsync(submission, "shared-key");
        Assert.NotEqual(a.Id, b.Id);
        Assert.Equal("a-project", a.ApplicationId);
        Assert.Equal("b-project", b.ApplicationId);
        Assert.Equal(JobStatus.Queued, a.Status);
        Assert.Equal(JobPriority.Critical, a.Priority);
        Assert.Equal(payload.GetRawText(), a.Payload.GetRawText());
        Assert.Equal("shared-key", a.IdempotencyKey);
        Assert.Null(a.Result);
        JobResponse repeated = await first.SubmitAsync(submission, "shared-key");
        Assert.Equal(a.Id, repeated.Id);
        Assert.Equal(a.CreatedAtUtc, repeated.CreatedAtUtc);

        ExecutionAssignmentResponse assignment = Assert.IsType<ExecutionAssignmentResponse>(await first.WaitAsync(new("Worker-A", [submission.Type], 0)));
        Assert.Equal(a.Id, assignment.JobId);
        Assert.Equal("a-project", assignment.ApplicationId);
        Assert.Equal(1, assignment.AttemptNumber);
        Assert.NotEqual(Guid.Empty, assignment.AttemptId);
        Assert.Equal(assignment.StartedAtUtc.AddSeconds(30), assignment.DeadlineAtUtc);
        Assert.Equal(assignment.DeadlineAtUtc.AddSeconds(1), assignment.LeaseExpiresAtUtc);
        Assert.Null(await first.WaitAsync(new("Worker-A", [submission.Type], 0)));
        JobResponse processing = await first.GetJobAsync(a.Id);
        Assert.Equal(JobStatus.Processing, processing.Status);
        Assert.Equal("Worker-A", processing.OwningWorkerId);
        Assert.Equal(assignment.LeaseExpiresAtUtc, processing.LeaseExpiresAtUtc);
        JobAttemptResponse running = Assert.Single(await first.GetAttemptsAsync(a.Id));
        Assert.Equal(assignment.AttemptId, running.AttemptId);
        Assert.Equal(JobAttemptOutcome.Running, running.Outcome);
        Assert.Null(running.FinishedAtUtc);

        CompleteExecutionRequest complete = new(assignment.WorkerId, JsonSerializer.SerializeToElement(new { sum = 42 }));
        JobResponse completed = await first.CompleteAsync(a.Id, assignment.AttemptId, complete);
        Assert.Equal(JobStatus.Completed, completed.Status);
        Assert.Equal(42, completed.Result!.Value.GetProperty("sum").GetInt32());
        Assert.Null(completed.OwningWorkerId);
        Assert.Null(completed.LeaseExpiresAtUtc);
        IReadOnlyList<JobAttemptResponse> before = await first.GetAttemptsAsync(a.Id);
        JobResponse duplicate = await first.CompleteAsync(a.Id, assignment.AttemptId, complete);
        Assert.Equal(completed.UpdatedAtUtc, duplicate.UpdatedAtUtc);
        Assert.Equal(completed.CompletedAtUtc, duplicate.CompletedAtUtc);
        Assert.Equal(before, await first.GetAttemptsAsync(a.Id));
        Assert.Equal(JobAttemptOutcome.Succeeded, Assert.Single(before).Outcome);
        Assert.Equal(assignment.DeadlineAtUtc, before[0].DeadlineAtUtc);
        Assert.NotNull(before[0].DurationMilliseconds);

        JsonElement rawJob = await http.GetFromJsonAsync<JsonElement>($"/api/jobs/{a.Id}");
        Assert.Equal("Completed", rawJob.GetProperty("status").GetString());
        Assert.Equal("Critical", rawJob.GetProperty("priority").GetString());
        Assert.Equal(completed.Result.Value.GetRawText(), rawJob.GetProperty("result").GetRawText());
        JsonElement rawHistory = await http.GetFromJsonAsync<JsonElement>($"/api/jobs/{a.Id}/attempts");
        Assert.Equal("Succeeded", rawHistory[0].GetProperty("outcome").GetString());

        ExecutionAssignmentResponse bAssignment = Assert.IsType<ExecutionAssignmentResponse>(await second.WaitAsync(new("Worker-B", [submission.Type], 0)));
        Assert.Equal(b.Id, bAssignment.JobId);
        Assert.NotEqual(assignment.AttemptId, bAssignment.AttemptId);
        JobResponse bCompleted = await second.CompleteAsync(b.Id, bAssignment.AttemptId, new(bAssignment.WorkerId));
        Assert.Null(bCompleted.Result);
        Assert.Equal(JobStatus.Completed, bCompleted.Status);
    }

    [Fact]
    public async Task Sdk_failure_uses_server_retries_and_deserializes_ordered_attempt_outcomes()
    {
        await using WebApplicationFactory<Program> factory = CreateFactory();
        using HttpClient http = factory.CreateClient();
        TaskForgeClient client = Client(http);
        JobResponse job = await client.SubmitAsync(new("sdk-fail", JsonSerializer.SerializeToElement(new { value = 1 }), MaxRetries: 1));
        ExecutionAssignmentResponse first = Assert.IsType<ExecutionAssignmentResponse>(await client.WaitAsync(new("worker-1", [job.Type], 0)));
        FailExecutionRequest failure = new(first.WorkerId, "Temporary", "Dependency unavailable.");

        JobResponse failed = await client.FailAsync(job.Id, first.AttemptId, failure);

        Assert.Equal(JobStatus.Retrying, failed.Status);
        Assert.Equal(1, failed.RetryCount);
        Assert.NotNull(failed.NextRetryAtUtc);
        ExecutionAssignmentResponse second = Assert.IsType<ExecutionAssignmentResponse>(await client.WaitAsync(new("worker-2", [job.Type], 5)));
        Assert.Equal(2, second.AttemptNumber);
        Assert.True(second.StartedAtUtc >= failed.NextRetryAtUtc);
        JobResponse duplicate = await client.FailAsync(job.Id, first.AttemptId, failure);
        Assert.Equal(JobStatus.Processing, duplicate.Status);
        Assert.Equal(second.WorkerId, duplicate.OwningWorkerId);
        Assert.Equal(1, duplicate.RetryCount);
        JobResponse terminal = await client.FailAsync(job.Id, second.AttemptId, new(second.WorkerId, "InvalidPayload", "Permanent failure."));
        Assert.Equal(JobStatus.DeadLettered, terminal.Status);
        Assert.Null(terminal.NextRetryAtUtc);
        IReadOnlyList<JobAttemptResponse> attempts = await client.GetAttemptsAsync(job.Id);
        Assert.Equal([JobAttemptOutcome.Failed, JobAttemptOutcome.PermanentlyFailed], attempts.Select(attempt => attempt.Outcome));
        Assert.Equal([first.AttemptId, second.AttemptId], attempts.Select(attempt => attempt.AttemptId));
        Assert.Equal("temporary", attempts[0].ErrorCode);
        Assert.Equal("invalidpayload", attempts[1].ErrorCode);
    }

    [Theory]
    [InlineData("complete")]
    [InlineData("fail")]
    public async Task Sdk_preserves_timeout_reason_from_real_server_without_mutating_new_attempt(string operation)
    {
        await using WebApplicationFactory<Program> factory = CreateFactory();
        using HttpClient http = factory.CreateClient();
        TaskForgeClient client = Client(http);
        JobResponse job = await client.SubmitAsync(new("sdk-timeout", JsonSerializer.SerializeToElement(new { value = 1 }), MaxRetries: 1, TimeoutSeconds: 3));
        ExecutionAssignmentResponse old = Assert.IsType<ExecutionAssignmentResponse>(await client.WaitAsync(new("old-worker", [job.Type], 0)));
        ExecutionAssignmentResponse current = Assert.IsType<ExecutionAssignmentResponse>(await client.WaitAsync(new("new-worker", [job.Type], 10)));
        Assert.Equal(2, current.AttemptNumber);
        string before = await http.GetStringAsync($"/api/jobs/{job.Id}");
        string history = await http.GetStringAsync($"/api/jobs/{job.Id}/attempts");

        TaskForgeApiException error = await Assert.ThrowsAsync<TaskForgeApiException>(() => operation == "complete"
            ? client.CompleteAsync(job.Id, old.AttemptId, new(old.WorkerId))
            : client.FailAsync(job.Id, old.AttemptId, new(old.WorkerId, "Temporary", "Late failure.")));

        Assert.Equal(HttpStatusCode.Conflict, error.StatusCode);
        Assert.Equal("AttemptTimedOut", error.Error.Code);
        Assert.Contains("AttemptTimedOut", error.Error.Detail);
        Assert.False(string.IsNullOrWhiteSpace(error.Error.TraceId));
        Assert.Equal(before, await http.GetStringAsync($"/api/jobs/{job.Id}"));
        Assert.Equal(history, await http.GetStringAsync($"/api/jobs/{job.Id}/attempts"));
        JobResponse completed = await client.CompleteAsync(job.Id, current.AttemptId, new(current.WorkerId));
        Assert.Equal(JobStatus.Completed, completed.Status);
        Assert.Equal([JobAttemptOutcome.TimedOut, JobAttemptOutcome.Succeeded], (await client.GetAttemptsAsync(job.Id)).Select(attempt => attempt.Outcome));
    }

    [Fact]
    public async Task Sdk_reads_real_validation_legacy_errors_and_cancellation_responses()
    {
        await using WebApplicationFactory<Program> factory = CreateFactory();
        using HttpClient http = factory.CreateClient();
        TaskForgeClient client = Client(http);
        SubmitJobRequest submission = new("sdk-cancel", JsonSerializer.SerializeToElement(new { value = 1 }));
        TaskForgeApiException validation = await Assert.ThrowsAsync<TaskForgeApiException>(() => client.SubmitAsync(submission with { Type = "" }));
        Assert.Equal(HttpStatusCode.BadRequest, validation.StatusCode);
        Assert.Contains("Type", validation.Error.Errors.Keys);
        TaskForgeApiException missing = await Assert.ThrowsAsync<TaskForgeApiException>(() => client.GetJobAsync(Guid.NewGuid()));
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Contains("was not found", missing.Error.Message);
        JobResponse queued = await client.SubmitAsync(submission, "idempotency-key");
        TaskForgeApiException conflict = await Assert.ThrowsAsync<TaskForgeApiException>(() => client.SubmitAsync(submission with { TimeoutSeconds = 15 }, "idempotency-key"));
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        Assert.Equal("idempotency-key", conflict.Error.AdditionalProperties["idempotencyKey"].GetString());

        JobResponse cancelled = await client.CancelAsync(queued.Id);
        Assert.Equal(JobStatus.Cancelled, cancelled.Status);
        Assert.True(cancelled.CancellationRequested);
        Assert.Empty(await client.GetAttemptsAsync(queued.Id));
        JobResponse processing = await client.SubmitAsync(submission);
        ExecutionAssignmentResponse active = Assert.IsType<ExecutionAssignmentResponse>(await client.WaitAsync(new("worker", [submission.Type], 0)));
        Assert.Equal(processing.Id, active.JobId);
        Assert.Equal(JobStatus.Cancelled, (await client.CancelAsync(processing.Id)).Status);
        JobAttemptResponse cancelledAttempt = Assert.Single(await client.GetAttemptsAsync(processing.Id));
        Assert.Equal(JobAttemptOutcome.Cancelled, cancelledAttempt.Outcome);
        Assert.Equal(active.AttemptId, cancelledAttempt.AttemptId);
        TaskForgeApiException rejected = await Assert.ThrowsAsync<TaskForgeApiException>(() => client.CompleteAsync(processing.Id, active.AttemptId, new(active.WorkerId)));
        Assert.Equal(HttpStatusCode.Conflict, rejected.StatusCode);
        Assert.Contains("Cancelled", rejected.Error.Detail);
        Assert.Null(rejected.Error.Code);
        Assert.Null(await client.WaitAsync(new("worker", [submission.Type], 0)));
    }

    private WebApplicationFactory<Program> CreateFactory() => new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
    {
        builder.UseSetting("ConnectionStrings:TaskForge", ConnectionString);
        builder.UseSetting("Worker:RetryDelaySeconds", "1");
        builder.UseSetting("Worker:MaxRetryDelaySeconds", "3");
        builder.UseSetting("Worker:LeaseGraceSeconds", "1");
        builder.UseSetting("Worker:PollIntervalMilliseconds", "50");
    });

    private static TaskForgeClient Client(HttpClient http, string applicationId = "sdk-project") => new(http, new() { BaseUrl = http.BaseAddress!, ApplicationId = applicationId });
}
