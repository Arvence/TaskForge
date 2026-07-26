using TaskForge.Domain.Jobs;

namespace TaskForge.Application.Abstractions.Persistence;

public interface IJobRepository
{
    Task AddAsync(Job job, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<Job>> GetAllAsync(
        CancellationToken cancellationToken = default);

    Task<Job?> FindAsync(Guid id, CancellationToken cancellationToken = default);
}
