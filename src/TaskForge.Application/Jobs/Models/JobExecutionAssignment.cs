using TaskForge.Domain.Jobs;

namespace TaskForge.Application.Jobs.Models;

public sealed record JobExecutionAssignment(Job Job, JobAttempt Attempt)
{
    public Guid AttemptId => Attempt.Id;
    public DateTimeOffset DeadlineAtUtc => Attempt.StartedAtUtc.AddSeconds(Job.TimeoutSeconds);
}
