using System.Text.Json;

using TaskForge.Application.Jobs.Models;

namespace TaskForge.Api.Jobs;

public sealed record ExecutionAssignmentResponse(Guid JobId, string ApplicationId, Guid AttemptId, int AttemptNumber, string WorkerId, string Type, JsonElement Payload, int TimeoutSeconds, DateTimeOffset StartedAtUtc, DateTimeOffset DeadlineAtUtc, DateTimeOffset LeaseExpiresAtUtc)
{
    public static ExecutionAssignmentResponse From(JobExecutionAssignment assignment) => new(
        assignment.Job.Id, assignment.Job.ApplicationId, assignment.AttemptId, assignment.Attempt.AttemptNumber,
        assignment.Attempt.WorkerId, assignment.Job.Type, JsonSerializer.Deserialize<JsonElement>(assignment.Job.PayloadJson),
        assignment.Job.TimeoutSeconds, assignment.Attempt.StartedAtUtc, assignment.DeadlineAtUtc, assignment.Job.LeaseExpiresAtUtc!.Value);
}
