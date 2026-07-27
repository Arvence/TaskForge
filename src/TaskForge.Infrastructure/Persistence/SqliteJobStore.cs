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

    public async Task<Job?> TryAcquireAsync(
        Guid id,
        string workerId,
        DateTimeOffset leaseExpiresAtUtc,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        Job? job = await FindAsync(id, cancellationToken);
        if (job?.Status != JobStatus.Queued)
        {
            return null;
        }

        long expectedVersion = job.Version;
        job.StartProcessing(workerId, leaseExpiresAtUtc, now);

        return await TryUpdateAsync(job, expectedVersion, cancellationToken)
            ? job
            : null;
    }

    public async Task<bool> TryUpdateAsync(
        Job job,
        long expectedVersion,
        CancellationToken cancellationToken = default)
    {
        dbContext.Jobs.Update(job);
        dbContext.Entry(job)
            .Property(candidate => candidate.Version)
            .OriginalValue = expectedVersion;

        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (DbUpdateConcurrencyException)
        {
            dbContext.ChangeTracker.Clear();
            return false;
        }
    }
}
