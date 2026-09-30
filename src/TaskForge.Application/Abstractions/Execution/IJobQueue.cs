using TaskForge.Application.Jobs.Models;

namespace TaskForge.Application.Abstractions.Execution;

public interface IJobQueue
{
    Task<JobExecutionAssignment?> TryDistributeAsync(string applicationId, string workerId, IReadOnlyCollection<string> supportedTypes, TimeSpan leaseGracePeriod, CancellationToken cancellationToken = default);

    Task<int> RecoverExpiredLeasesAsync(DateTimeOffset now, CancellationToken cancellationToken = default);
}
