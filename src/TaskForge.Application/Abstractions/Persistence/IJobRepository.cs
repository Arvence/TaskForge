using TaskForge.Application.Jobs.Models;
using TaskForge.Domain.Jobs;

namespace TaskForge.Application.Abstractions.Persistence;

public interface IJobRepository
{
    Task AddAsync(Job job, CancellationToken cancellationToken = default);

    Task<Job> AddOrGetExistingAsync(
        Job job,
        CancellationToken cancellationToken = default);

    Task<JobPage> GetPageAsync(
        ListJobsQuery query,
        CancellationToken cancellationToken = default);

    Task<Job?> FindAsync(Guid id, CancellationToken cancellationToken = default);

    Task<Job?> FindByIdempotencyKeyAsync(
        string idempotencyKey,
        CancellationToken cancellationToken = default);

    Task<bool> TryUpdateAsync(
        Job job,
        long expectedVersion,
        CancellationToken cancellationToken = default);
}
