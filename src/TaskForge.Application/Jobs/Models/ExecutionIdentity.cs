using TaskForge.Domain.Jobs;

namespace TaskForge.Application.Jobs.Models;

public sealed record ExecutionIdentity
{
    public ExecutionIdentity(string applicationId, Guid jobId, Guid attemptId, string workerId)
    {
        ApplicationId = JobApplicationId.Normalize(applicationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(workerId);
        if (jobId == Guid.Empty || attemptId == Guid.Empty || workerId.Length > 200)
        {
            throw new ArgumentException("Execution identity requires nonempty job and attempt IDs and a worker ID of at most 200 characters.");
        }

        JobId = jobId;
        AttemptId = attemptId;
        WorkerId = workerId;
    }

    public string ApplicationId { get; }
    public Guid JobId { get; }
    public Guid AttemptId { get; }
    public string WorkerId { get; }
}
