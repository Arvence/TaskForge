using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

using TaskForge.Application.Abstractions.Execution;
using TaskForge.Application.Abstractions.Persistence;
using TaskForge.Application.Jobs.Models;
using TaskForge.Domain.Jobs;

namespace TaskForge.Infrastructure.Persistence;

public sealed class EfCoreJobStore(TaskForgeDbContext dbContext)
    : IJobRepository, IJobQueue, IJobAttemptReader
{
    public async Task AddAsync(Job job, CancellationToken cancellationToken = default)
    {
        dbContext.Jobs.Add(job);
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task<Job> AddOrGetExistingAsync(Job job, CancellationToken cancellationToken = default)
    {
        if (job.IdempotencyKey is null)
        {
            await AddAsync(job, cancellationToken);
            return job;
        }

        Job? existing = await FindByIdempotencyKeyAsync(
            job.ApplicationId,
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
                job.ApplicationId,
                job.IdempotencyKey,
                cancellationToken);
            if (existing is not null)
            {
                return existing;
            }

            throw;
        }
    }

    public async Task<JobPage> GetPageAsync(ListJobsQuery query, CancellationToken cancellationToken = default)
    {
        IQueryable<Job> jobs = dbContext.Jobs.AsNoTracking();

        if (query.ApplicationId is not null)
        {
            jobs = jobs.Where(job => job.ApplicationId == query.ApplicationId);
        }

        if (query.Status is not null)
        {
            jobs = jobs.Where(job => job.Status == query.Status);
        }

        if (query.Type is not null)
        {
            jobs = jobs.Where(job => job.Type == query.Type);
        }

        if (query.Priority is not null)
        {
            jobs = jobs.Where(job => job.Priority == query.Priority);
        }

        int totalCount = await jobs.CountAsync(cancellationToken);
        long offset = ((long)query.Page - 1) * query.PageSize;
        IReadOnlyList<Job> items = offset > int.MaxValue
            ? []
            : await jobs
                .OrderByDescending(job => job.CreatedAtUtc)
                .ThenByDescending(job => job.Id)
                .Skip((int)offset)
                .Take(query.PageSize)
                .ToListAsync(cancellationToken);

        return new JobPage(items, query.Page, query.PageSize, totalCount);
    }

    public Task<Job?> FindAsync(Guid id, CancellationToken cancellationToken = default) =>
        dbContext.Jobs
            .AsNoTracking()
            .SingleOrDefaultAsync(job => job.Id == id, cancellationToken);

    public Task<Job?> FindByIdempotencyKeyAsync(string applicationId, string idempotencyKey, CancellationToken cancellationToken = default) =>
        dbContext.Jobs
            .AsNoTracking()
            .SingleOrDefaultAsync(
                job => job.ApplicationId == applicationId && job.IdempotencyKey == idempotencyKey,
                cancellationToken);

    public Task<bool> TryUpdateAsync(Job job, long expectedVersion, CancellationToken cancellationToken = default) =>
        TryUpdateAsync(job, expectedVersion, cancellationToken, null);

    public async Task<bool> TryUpdateAsync(Job job, long expectedVersion, CancellationToken cancellationToken, JobAttempt? attempt)
    {
        if (attempt is not null)
        {
            dbContext.JobAttempts.Update(attempt);
        }

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

    public async Task<Job?> TryAcquireNextAsync(string workerId, TimeSpan leaseGracePeriod, DateTimeOffset now, CancellationToken cancellationToken = default)
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

    public async Task<int> RecoverExpiredLeasesAsync(DateTimeOffset now, CancellationToken cancellationToken = default)
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
            List<JobAttempt> attempts = await dbContext.JobAttempts
                .Where(attempt => attempt.JobId == job.Id && attempt.Outcome == JobAttemptOutcome.Running)
                .ToListAsync(cancellationToken);
            foreach (JobAttempt attempt in attempts)
            {
                attempt.Finish(JobAttemptOutcome.Abandoned, now, "LeaseExpired", "The worker lease expired before the attempt finished.");
            }

            job.RecoverExpiredLease(now);

            if (await TryUpdateAsync(job, expectedVersion, cancellationToken))
            {
                recoveredCount++;
            }
        }

        return recoveredCount;
    }

    public Task<bool> IsCancellationRequestedAsync(Guid jobId, CancellationToken cancellationToken = default) =>
        dbContext.Jobs
            .AsNoTracking()
            .Where(job => job.Id == jobId)
            .Select(job => job.CancellationRequested)
            .SingleOrDefaultAsync(cancellationToken);

    public async Task<bool> TryCancelProcessingAsync(Guid jobId, DateTimeOffset now, CancellationToken cancellationToken = default, Guid? attemptId = null)
    {
        dbContext.ChangeTracker.Clear();
        Job? job = await FindAsync(jobId, cancellationToken);
        if (job?.Status != JobStatus.Processing || !job.CancellationRequested)
        {
            return false;
        }

        long expectedVersion = job.Version;
        JobAttempt? attempt = null;
        if (attemptId is not null)
        {
            attempt = await dbContext.JobAttempts.SingleOrDefaultAsync(candidate => candidate.Id == attemptId && candidate.JobId == jobId && candidate.Outcome == JobAttemptOutcome.Running, cancellationToken);
            if (attempt is null || attempt.WorkerId != job.OwningWorkerId)
            {
                return false;
            }

            attempt.Finish(JobAttemptOutcome.Cancelled, now, "CancellationRequested", "Job cancellation was requested.");
        }

        job.Cancel(now);
        return await TryUpdateAsync(job, expectedVersion, cancellationToken, attempt);
    }

    public async Task<JobAttempt?> TryStartAttemptAsync(Job job, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        int lastNumber = await dbContext.JobAttempts
            .Where(attempt => attempt.JobId == job.Id)
            .Select(attempt => (int?)attempt.AttemptNumber)
            .MaxAsync(cancellationToken) ?? 0;
        JobAttempt attempt = new(Guid.NewGuid(), job.Id, lastNumber + 1, job.OwningWorkerId!, now);
        long expectedVersion = job.Version;
        job.RecordAttemptStart(now);
        dbContext.JobAttempts.Add(attempt);
        dbContext.Jobs.Update(job);
        dbContext.Entry(job).Property(candidate => candidate.Version).OriginalValue = expectedVersion;

        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
            return attempt;
        }
        catch (DbUpdateConcurrencyException)
        {
            dbContext.ChangeTracker.Clear();
            return null;
        }
        catch (DbUpdateException exception) when (exception.InnerException is SqlException { Number: 2601 or 2627 })
        {
            dbContext.ChangeTracker.Clear();
            return null;
        }
    }

    public async Task FinishAttemptAsync(JobAttempt attempt, CancellationToken cancellationToken = default)
    {
        dbContext.Entry(attempt).State = EntityState.Detached;
        await dbContext.JobAttempts
            .Where(candidate => candidate.Id == attempt.Id && candidate.Outcome == JobAttemptOutcome.Running)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(candidate => candidate.Outcome, attempt.Outcome)
                .SetProperty(candidate => candidate.FinishedAtUtc, attempt.FinishedAtUtc)
                .SetProperty(candidate => candidate.DurationMilliseconds, attempt.DurationMilliseconds)
                .SetProperty(candidate => candidate.ErrorCode, attempt.ErrorCode)
                .SetProperty(candidate => candidate.ErrorMessage, attempt.ErrorMessage), cancellationToken);
    }

    public async Task<IReadOnlyList<JobAttempt>?> GetAttemptsAsync(Guid jobId, CancellationToken cancellationToken = default)
    {
        if (!await dbContext.Jobs.AnyAsync(job => job.Id == jobId, cancellationToken))
        {
            return null;
        }

        return await dbContext.JobAttempts.AsNoTracking()
            .Where(attempt => attempt.JobId == jobId)
            .OrderBy(attempt => attempt.AttemptNumber)
            .ToListAsync(cancellationToken);
    }
}
