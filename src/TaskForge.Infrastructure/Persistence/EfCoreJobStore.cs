using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using TaskForge.Application.Abstractions.Execution;
using TaskForge.Application.Abstractions.Persistence;
using TaskForge.Application.Jobs;
using TaskForge.Application.Jobs.Models;
using TaskForge.Application.Workers;
using TaskForge.Domain.Jobs;

namespace TaskForge.Infrastructure.Persistence;

public sealed class EfCoreJobStore(TaskForgeDbContext dbContext, JobRetryPolicy? retryPolicy = null, ILogger<EfCoreJobStore>? logger = null)
    : IJobRepository, IJobQueue, IJobAttemptReader
{
    private const int MaximumDistributionAttempts = 5;
    private const int MaximumRecoveryAttempts = 5;
    private readonly JobRetryPolicy _retryPolicy = retryPolicy ?? new(Options.Create(new WorkerOptions()));
    private readonly ILogger<EfCoreJobStore> _logger = logger ?? NullLogger<EfCoreJobStore>.Instance;

    public async Task<JobExecutionAssignment?> TryDistributeAsync(string applicationId, string workerId, IReadOnlyCollection<string> supportedTypes, TimeSpan leaseGracePeriod, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        applicationId = JobApplicationId.Normalize(applicationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(workerId);
        ArgumentNullException.ThrowIfNull(supportedTypes);
        if (workerId.Length > 200)
        {
            throw new ArgumentException("Worker ID cannot exceed 200 characters.", nameof(workerId));
        }

        if (leaseGracePeriod <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(leaseGracePeriod), "The lease grace period must be positive.");
        }

        if (supportedTypes.Any(type => string.IsNullOrWhiteSpace(type) || type.Length > 100))
        {
            throw new ArgumentException("Supported types must contain 1 to 100 characters.", nameof(supportedTypes));
        }

        string[] types = supportedTypes.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (types.Length == 0)
        {
            return null;
        }

        return await dbContext.Database.CreateExecutionStrategy().ExecuteAsync(
            () => TryDistributeCoreAsync(applicationId, workerId, types, leaseGracePeriod, now, cancellationToken));
    }

    private async Task<JobExecutionAssignment?> TryDistributeCoreAsync(string applicationId, string workerId, string[] types, TimeSpan leaseGracePeriod, DateTimeOffset now, CancellationToken cancellationToken)
    {
        for (int acquisition = 0; acquisition < MaximumDistributionAttempts; acquisition++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            dbContext.ChangeTracker.Clear();
            await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
            try
            {
                Job? job = await dbContext.Jobs.AsNoTracking()
                    .Where(candidate => candidate.ApplicationId == applicationId && types.Contains(candidate.Type))
                    .Where(candidate => candidate.Status == JobStatus.Queued
                        || (candidate.Status == JobStatus.Retrying && candidate.NextRetryAtUtc != null && candidate.NextRetryAtUtc <= now))
                    .OrderByDescending(candidate => candidate.Priority)
                    .ThenBy(candidate => candidate.NextRetryAtUtc ?? candidate.QueuedAtUtc)
                    .ThenBy(candidate => candidate.CreatedAtUtc)
                    .ThenBy(candidate => candidate.Id)
                    .FirstOrDefaultAsync(cancellationToken);
                if (job is null)
                {
                    return null;
                }

                int lastNumber = await dbContext.JobAttempts
                    .Where(attempt => attempt.JobId == job.Id)
                    .Select(attempt => (int?)attempt.AttemptNumber)
                    .MaxAsync(cancellationToken) ?? 0;
                JobAttempt attempt = new(Guid.NewGuid(), job.Id, checked(lastNumber + 1), workerId, now);
                JobExecutionAssignment assignment = new(job, attempt);
                long expectedVersion = job.Version;
                if (job.Status == JobStatus.Retrying)
                {
                    job.QueueRetry(now);
                }

                job.StartProcessing(workerId, assignment.DeadlineAtUtc.Add(leaseGracePeriod), attempt.StartedAtUtc);
                dbContext.Jobs.Update(job);
                dbContext.Entry(job).Property(candidate => candidate.Version).OriginalValue = expectedVersion;
                dbContext.JobAttempts.Add(attempt);
                await dbContext.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                return assignment;
            }
            catch (Exception exception) when (IsDistributionConflict(exception))
            {
                await transaction.RollbackAsync(CancellationToken.None);
                dbContext.ChangeTracker.Clear();
            }
            catch
            {
                await transaction.RollbackAsync(CancellationToken.None);
                dbContext.ChangeTracker.Clear();
                throw;
            }
        }

        return null;
    }

    private static bool IsDistributionConflict(Exception exception) =>
        exception is DbUpdateConcurrencyException
        || exception is SqlException { Number: 1205 }
        || exception is DbUpdateException { InnerException: SqlException { Number: 1205 or 2601 or 2627 } };

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
        List<Guid> expiredJobIds = await dbContext.Jobs
            .AsNoTracking()
            .Where(job =>
                job.Status == JobStatus.Processing
                && (job.LeaseExpiresAtUtc == null || job.LeaseExpiresAtUtc <= now))
            .Select(job => job.Id)
            .ToListAsync(cancellationToken);

        int recoveredCount = 0;
        foreach (Guid jobId in expiredJobIds)
        {
            if (await dbContext.Database.CreateExecutionStrategy().ExecuteAsync(() => RecoverExpiredLeaseAsync(jobId, now, cancellationToken)))
            {
                recoveredCount++;
            }
        }

        return recoveredCount;
    }

    private async Task<bool> RecoverExpiredLeaseAsync(Guid jobId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        for (int retry = 0; retry < MaximumRecoveryAttempts; retry++)
        {
            dbContext.ChangeTracker.Clear();
            await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
            try
            {
                Job? job = await dbContext.Jobs.FromSql($"""
                    SELECT * FROM [Jobs] WITH (UPDLOCK, HOLDLOCK) WHERE [Id] = {jobId}
                    """).SingleOrDefaultAsync(cancellationToken);
                if (job is null || job.Status != JobStatus.Processing || job.LeaseExpiresAtUtc > now)
                {
                    return false;
                }

                List<JobAttempt> attempts = await dbContext.JobAttempts.FromSql($"""
                    SELECT * FROM [JobAttempts] WITH (UPDLOCK, HOLDLOCK) WHERE [JobId] = {jobId}
                    """).OrderBy(attempt => attempt.AttemptNumber).ToListAsync(cancellationToken);
                if (!CanRecoverOwnership(job, attempts, now))
                {
                    _logger.LogWarning("Skipped lease recovery for job {JobId}: inconsistent ownership or attempt history. Worker {WorkerId}, start {StartedAtUtc}, lease {LeaseExpiresAtUtc}.", job.Id, job.OwningWorkerId, job.StartedAtUtc, job.LeaseExpiresAtUtc);
                    return false;
                }

                JobAttempt? current = attempts.LastOrDefault();
                bool missingAttempt = current is null || current.StartedAtUtc < job.StartedAtUtc;
                if (missingAttempt)
                {
                    current = JobAttempt.CreateLeaseRecovery(job, checked((current?.AttemptNumber ?? 0) + 1), now);
                    dbContext.JobAttempts.Add(current);
                }
                else if (current!.Outcome == JobAttemptOutcome.Running)
                {
                    current.Finish(job.CancellationRequested ? JobAttemptOutcome.Cancelled : JobAttemptOutcome.TimedOut, now,
                        job.CancellationRequested ? "CancellationRequested" : "Timeout",
                        job.CancellationRequested ? "Job cancellation was requested." : $"Job timed out after {job.TimeoutSeconds} second(s).");
                }

                job.RecoverExpiredLease(now.Add(_retryPolicy.GetDelay(job.RetryCount)), now);
                await dbContext.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                dbContext.ChangeTracker.Clear();
                return true;
            }
            catch (Exception exception) when (IsDistributionConflict(exception))
            {
                await transaction.RollbackAsync(CancellationToken.None);
                dbContext.ChangeTracker.Clear();
            }
            catch
            {
                await transaction.RollbackAsync(CancellationToken.None);
                dbContext.ChangeTracker.Clear();
                throw;
            }
        }

        _logger.LogWarning("Skipped lease recovery for job {JobId} after repeated concurrent changes.", jobId);
        return false;
    }

    private static bool CanRecoverOwnership(Job job, IReadOnlyList<JobAttempt> attempts, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(job.OwningWorkerId) || job.StartedAtUtc is null
            || job.LeaseExpiresAtUtc is null || job.TimeoutSeconds <= 0
            || job.LeaseExpiresAtUtc < job.StartedAtUtc.Value.AddSeconds(job.TimeoutSeconds))
        {
            return false;
        }

        JobAttempt? current = attempts.LastOrDefault();
        foreach (JobAttempt attempt in attempts)
        {
            bool running = attempt.Outcome == JobAttemptOutcome.Running;
            bool validFinish = running
                ? attempt.FinishedAtUtc is null
                : attempt.FinishedAtUtc >= attempt.StartedAtUtc && attempt.FinishedAtUtc <= now;
            if (!validFinish || (running && !job.CancellationRequested && attempt.StartedAtUtc.AddSeconds(job.TimeoutSeconds) > now))
            {
                return false;
            }

            if (attempt.StartedAtUtc < job.StartedAtUtc)
            {
                if (attempt.Outcome == JobAttemptOutcome.Running || attempt.FinishedAtUtc is null || attempt.FinishedAtUtc > job.StartedAtUtc)
                {
                    return false;
                }
            }
            else
            {
                bool recoverableOutcome = attempt.Outcome is JobAttemptOutcome.Running or JobAttemptOutcome.Abandoned or JobAttemptOutcome.TimedOut
                    || (attempt.Outcome == JobAttemptOutcome.Cancelled && job.CancellationRequested);
                if (attempt != current || attempt.WorkerId != job.OwningWorkerId || attempt.StartedAtUtc >= job.LeaseExpiresAtUtc || !recoverableOutcome)
                {
                    return false;
                }
            }
        }

        return true;
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
