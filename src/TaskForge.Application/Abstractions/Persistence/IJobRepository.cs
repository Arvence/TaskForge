using TaskForge.Domain.Jobs;

namespace TaskForge.Application.Abstractions.Persistence;

public interface IJobRepository
{
    Task AddAsync(Job job, CancellationToken cancellationToken = default);

    Task<Job> AddOrGetExistingAsync(
        Job job,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<Job>> GetAllAsync(
        CancellationToken cancellationToken = default);

    Task<Job?> FindAsync(Guid id, CancellationToken cancellationToken = default);

    Task<Job?> FindByIdempotencyKeyAsync(
        string idempotencyKey,
        CancellationToken cancellationToken = default);

    Task<Job?> TryAcquireAsync(
        Guid id,
        string workerId,
        DateTimeOffset leaseExpiresAtUtc,
        DateTimeOffset now,
        CancellationToken cancellationToken = default);

    Task<bool> TryUpdateAsync(
        Job job,
        long expectedVersion,
        CancellationToken cancellationToken = default);
}