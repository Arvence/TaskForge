using TaskForge.Application.Jobs.Models;

namespace TaskForge.Application.Abstractions.Persistence;

public interface IJobAttemptReader
{
    Task<JobAttemptHistory?> GetAttemptsAsync(Guid jobId, CancellationToken cancellationToken = default);
}
