using Microsoft.EntityFrameworkCore;
using TaskForge.Application.Abstractions.Persistence;
using TaskForge.Domain.Jobs;

namespace TaskForge.Infrastructure.Persistence;

public sealed class SqliteJobStore(TaskForgeDbContext dbContext) : IJobRepository
{
    public async Task AddAsync(Job job, CancellationToken cancellationToken = default)
    {
        dbContext.Jobs.Add(job);
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<Job>> GetAllAsync(
        CancellationToken cancellationToken = default) =>
        await dbContext.Jobs
            .AsNoTracking()
            .OrderByDescending(job => job.CreatedAtUtc)
            .ToListAsync(cancellationToken);

    public Task<Job?> FindAsync(Guid id, CancellationToken cancellationToken = default) =>
        dbContext.Jobs
            .AsNoTracking()
            .SingleOrDefaultAsync(job => job.Id == id, cancellationToken);
}
