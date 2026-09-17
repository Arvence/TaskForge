using TaskForge.Domain.Jobs;

namespace TaskForge.Application.Abstractions.Persistence;

public interface IJobAttemptReader
{
    Task<IReadOnlyList<JobAttempt>?> GetAttemptsAsync(Guid jobId, CancellationToken cancellationToken = default);
}
