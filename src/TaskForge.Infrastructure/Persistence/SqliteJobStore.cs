using Microsoft.EntityFrameworkCore;

using TaskForge.Application.Abstractions.Execution;
using TaskForge.Application.Abstractions.Persistence;
using TaskForge.Domain.Jobs;

namespace TaskForge.Infrastructure.Persistence;

public sealed class SqliteJobStore(TaskForgeDbContext dbContext)
    : IJobRepository, IJobQueue
{
    public async Task AddAsync(Job job, CancellationToken cancellationToken = default)
    {
        dbContext.Jobs.Add(job);
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task<Job> AddOrGetExistingAsync(
        Job job,
        CancellationToken cancellationToken = default)
    {
        if (job.IdempotencyKey is null)
        {
            await AddAsync(job, cancellationToken);
            return job;
        }

        Job? existing = await FindByIdempotencyKeyAsync(
            job.IdempotencyKey,
            cancellationToken);
        if (existing is not null)
        {
            return existing;
        }

        dbContext.Jobs.Add(job);

        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
            return job;
        }
        catch (DbUpdateException)
        {
            dbContext.ChangeTracker.Clear();
            existing = await FindByIdempotencyKeyAsync(
                job.IdempotencyKey,
                cancellationToken);
            if (existing is not null)
            {
                return existing;
            }

            throw;
        }
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

    public Task<Job?> FindByIdempotencyKeyAsync(
        string idempotencyKey,
        CancellationToken cancellationToken = default) =>
        dbContext.Jobs
            .AsNoTracking()
            .SingleOrDefaultAsync(
                job => job.IdempotencyKey == idempotencyKey,
                cancellationToken);

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

    public async Task<Job?> TryAcquireNextAsync(
        string workerId,
        TimeSpan leaseGracePeriod,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workerId);

        if (leaseGracePeriod <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(leaseGracePeriod),
                "The lease grace period must be positive.");
        }

        Job? job = await dbContext.Jobs
            .AsNoTracking()
            .Where(candidate =>
                candidate.Status == JobStatus.Queued
                || (candidate.Status == JobStatus.Retrying
                    && candidate.NextRetryAtUtc != null
                    && candidate.NextRetryAtUtc <= now))
            .OrderByDescending(candidate => candidate.Priority)
            .ThenBy(candidate => candidate.NextRetryAtUtc ?? candidate.QueuedAtUtc)
            .ThenBy(candidate => candidate.CreatedAtUtc)
            .FirstOrDefaultAsync(cancellationToken);

        if (job is null)
        {
            return null;
        }

        long expectedVersion = job.Version;
        if (job.Status == JobStatus.Retrying)
        {
            job.QueueRetry(now);
        }

        DateTimeOffset leaseExpiresAtUtc = now
            .AddSeconds(job.TimeoutSeconds)
            .Add(leaseGracePeriod);
        job.StartProcessing(workerId, leaseExpiresAtUtc, now);

        return await TryUpdateAsync(job, expectedVersion, cancellationToken)
            ? job
            : null;
    }

    public async Task<int> RecoverExpiredLeasesAsync(
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        List<Job> expiredJobs = await dbContext.Jobs
            .AsNoTracking()
            .Where(job =>
                job.Status == JobStatus.Processing
                && job.LeaseExpiresAtUtc != null
                && job.LeaseExpiresAtUtc <= now)
            .ToListAsync(cancellationToken);

        int recoveredCount = 0;
        foreach (Job job in expiredJobs)
        {
            long expectedVersion = job.Version;
            job.RecoverExpiredLease(now);

            if (await TryUpdateAsync(job, expectedVersion, cancellationToken))
            {
                recoveredCount++;
            }
        }

        return recoveredCount;
    }

    public Task<bool> IsCancellationRequestedAsync(
        Guid jobId,
        CancellationToken cancellationToken = default) =>
        dbContext.Jobs
            .AsNoTracking()
            .Where(job => job.Id == jobId)
            .Select(job => job.CancellationRequested)
            .SingleOrDefaultAsync(cancellationToken);

    public async Task<bool> TryCancelProcessingAsync(
        Guid jobId,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        dbContext.ChangeTracker.Clear();
        Job? job = await FindAsync(jobId, cancellationToken);
        if (job?.Status != JobStatus.Processing || !job.CancellationRequested)
        {
            return false;
        }

        long expectedVersion = job.Version;
        job.Cancel(now);
        return await TryUpdateAsync(job, expectedVersion, cancellationToken);
    }
}