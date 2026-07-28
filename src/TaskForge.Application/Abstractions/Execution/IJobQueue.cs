using TaskForge.Domain.Jobs;

namespace TaskForge.Application.Abstractions.Execution;

public interface IJobQueue
{
    Task<Job?> TryAcquireNextAsync(
        string workerId,
        TimeSpan leaseGracePeriod,
        DateTimeOffset now,
        CancellationToken cancellationToken = default);

    Task<int> RecoverExpiredLeasesAsync(
        DateTimeOffset now,
        CancellationToken cancellationToken = default);

    Task<bool> IsCancellationRequestedAsync(
        Guid jobId,
        CancellationToken cancellationToken = default);

    Task<bool> TryCancelProcessingAsync(
        Guid jobId,
        DateTimeOffset now,
        CancellationToken cancellationToken = default);

    Task<bool> TryUpdateAsync(
        Job job,
        long expectedVersion,
        CancellationToken cancellationToken = default);
}