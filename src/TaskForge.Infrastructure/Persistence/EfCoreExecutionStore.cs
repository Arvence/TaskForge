using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

using TaskForge.Application.Abstractions.Persistence;
using TaskForge.Application.Jobs;
using TaskForge.Application.Jobs.Models;
using TaskForge.Domain.Jobs;

namespace TaskForge.Infrastructure.Persistence;

public sealed class EfCoreExecutionStore(TaskForgeDbContext dbContext, JobRetryPolicy retryPolicy) : IExecutionStore
{
    private const int MaximumTransitionAttempts = 5;

    public Task<JobCancellationResult> CancelAsync(Guid jobId, CancellationToken cancellationToken = default) =>
        dbContext.Database.CreateExecutionStrategy().ExecuteAsync(() => CancelCoreAsync(jobId, cancellationToken));

    private async Task<JobCancellationResult> CancelCoreAsync(Guid jobId, CancellationToken cancellationToken)
    {
        for (int retry = 0; retry < MaximumTransitionAttempts; retry++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            dbContext.ChangeTracker.Clear();
            await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
            try
            {
                Job? job = await dbContext.Jobs.FromSql($"""
                    SELECT * FROM [Jobs] WITH (UPDLOCK, HOLDLOCK)
                    WHERE [Id] = {jobId}
                    """).SingleOrDefaultAsync(cancellationToken);
                if (job is null)
                {
                    return new(JobCancellationStatus.NotFound, null);
                }

                if (job.Status == JobStatus.Cancelled)
                {
                    return new(JobCancellationStatus.Accepted, job);
                }

                if (job.Status is JobStatus.Completed or JobStatus.DeadLettered)
                {
                    return new(JobCancellationStatus.AlreadyFinished, job);
                }

                JobAttempt? attempt = null;
                if (job.Status == JobStatus.Processing)
                {
                    attempt = await dbContext.JobAttempts.FromSql($"""
                        SELECT * FROM [JobAttempts] WITH (UPDLOCK, HOLDLOCK)
                        WHERE [JobId] = {jobId}
                        """).OrderByDescending(candidate => candidate.AttemptNumber).FirstOrDefaultAsync(cancellationToken);
                    if (job.StartedAtUtc is null || job.OwningWorkerId is null || job.LeaseExpiresAtUtc is null)
                    {
                        return new(JobCancellationStatus.ConcurrentUpdate, null);
                    }

                    if (attempt is not null && attempt.StartedAtUtc < job.StartedAtUtc
                        && attempt.Outcome != JobAttemptOutcome.Running && attempt.FinishedAtUtc <= job.StartedAtUtc)
                    {
                        attempt = null;
                    }

                    if (attempt is not null && (attempt.WorkerId != job.OwningWorkerId || attempt.StartedAtUtc < job.StartedAtUtc
                        || attempt.StartedAtUtc >= job.LeaseExpiresAtUtc))
                    {
                        return new(JobCancellationStatus.ConcurrentUpdate, null);
                    }
                }

                DateTimeOffset now = await GetSqlUtcNowAsync(cancellationToken);
                if (job.Status == JobStatus.Processing && !job.CancellationRequested
                    && now >= (attempt?.StartedAtUtc ?? job.StartedAtUtc!.Value).AddSeconds(job.TimeoutSeconds))
                {
                    job.TimeOut(now.Add(retryPolicy.GetDelay(job.RetryCount)), now);
                    if (attempt?.Outcome == JobAttemptOutcome.Running)
                    {
                        attempt.Finish(JobAttemptOutcome.TimedOut, now, "Timeout", $"Job timed out after {job.TimeoutSeconds} second(s).");
                    }
                }

                JobCancellationStatus status = JobCancellationStatus.AlreadyFinished;
                if (job.Status != JobStatus.DeadLettered)
                {
                    job.RequestCancellation(now);
                    if (job.Status == JobStatus.Processing)
                    {
                        job.Cancel(now);
                        if (attempt?.Outcome == JobAttemptOutcome.Running)
                        {
                            attempt.Finish(JobAttemptOutcome.Cancelled, now, "CancellationRequested", "Job cancellation was requested.");
                        }
                    }

                    status = JobCancellationStatus.Accepted;
                }

                await dbContext.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                return new(status, job);
            }
            catch (Exception exception) when (IsTransitionConflict(exception))
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

        return new(JobCancellationStatus.ConcurrentUpdate, null);
    }

    public async Task<ExecutionLookup> FindExecutionAsync(ExecutionIdentity identity, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(identity);
        return await dbContext.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            dbContext.ChangeTracker.Clear();
            await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
            return await ReadExecutionAsync(identity, cancellationToken);
        });
    }

    public async Task<ExecutionResult> TransitionAsync(ExecutionIdentity identity, ExecutionReport report, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(report);
        return await dbContext.Database.CreateExecutionStrategy().ExecuteAsync(
            () => TransitionCoreAsync(identity, report, cancellationToken));
    }

    private async Task<ExecutionResult> TransitionCoreAsync(ExecutionIdentity identity, ExecutionReport report, CancellationToken cancellationToken)
    {
        for (int retry = 0; retry < MaximumTransitionAttempts; retry++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            dbContext.ChangeTracker.Clear();
            await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
            try
            {
                ExecutionLookup lookup = await ReadExecutionAsync(identity, cancellationToken);
                if (lookup.Assignment is not { } assignment)
                {
                    return lookup.Result;
                }

                Job job = assignment.Job;
                JobAttempt attempt = assignment.Attempt;
                if (attempt.Outcome != JobAttemptOutcome.Running)
                {
                    return ResolveFinishedReport(job, attempt, report, lookup.Result);
                }

                if (lookup.Result == ExecutionResult.Accepted && report is ExecutionReport.Timeout or ExecutionReport.Cancel)
                {
                    return ExecutionResult.Conflicting;
                }

                long expectedVersion = job.Version;
                TimeSpan retryDelay = retryPolicy.GetDelay(job.RetryCount);
                JobAttemptOutcome outcome = ResolveOutcome(lookup.Result, report);
                (string? errorCode, string? errorMessage) = ResolveError(job, report, outcome);
                DateTimeOffset provisionalTime = await GetSqlUtcNowAsync(cancellationToken);
                ApplyJobTransition(job, report, outcome, errorCode, errorMessage, retryDelay, provisionalTime);

                List<DateTimeOffset> timestamps = await dbContext.Database.SqlQuery<DateTimeOffset>($"""
                    DECLARE @transitionUtc datetimeoffset(7) = TODATETIMEOFFSET(SYSUTCDATETIME(), '+00:00');
                    UPDATE [Jobs]
                    SET [Status] = {job.Status.ToString()}, [Version] = {job.Version},
                        [UpdatedAtUtc] = @transitionUtc,
                        [CompletedAtUtc] = CASE WHEN {outcome == JobAttemptOutcome.Succeeded} = 1 THEN @transitionUtc ELSE [CompletedAtUtc] END,
                        [NextRetryAtUtc] = CASE WHEN {job.Status == JobStatus.Retrying} = 1 THEN DATEADD(millisecond, {(int)retryDelay.TotalMilliseconds}, @transitionUtc) ELSE NULL END,
                        [RetryCount] = {job.RetryCount}, [ResultJson] = {job.ResultJson}, [LastError] = {job.LastError},
                        [OwningWorkerId] = NULL, [LeaseExpiresAtUtc] = NULL
                    OUTPUT INSERTED.[UpdatedAtUtc] AS [Value]
                    WHERE [Id] = {identity.JobId} AND [ApplicationId] = {identity.ApplicationId}
                        AND [Version] = {expectedVersion} AND [Status] = N'Processing'
                        AND [OwningWorkerId] COLLATE Latin1_General_100_BIN2 = {identity.WorkerId}
                        AND [StartedAtUtc] = {attempt.StartedAtUtc}
                        AND [CancellationRequested] = {outcome == JobAttemptOutcome.Cancelled}
                        AND ({outcome == JobAttemptOutcome.Cancelled} = 1
                            OR ({outcome == JobAttemptOutcome.TimedOut} = 1 AND @transitionUtc >= {assignment.DeadlineAtUtc})
                            OR ({outcome != JobAttemptOutcome.TimedOut} = 1 AND @transitionUtc < {assignment.DeadlineAtUtc}))
                        AND EXISTS (SELECT 1 FROM [JobAttempts] WHERE [Id] = {identity.AttemptId}
                            AND [JobId] = {identity.JobId} AND [WorkerId] COLLATE Latin1_General_100_BIN2 = {identity.WorkerId}
                            AND [Outcome] = N'Running')
                        AND NOT EXISTS (SELECT 1 FROM [JobAttempts] WHERE [JobId] = {identity.JobId}
                            AND [AttemptNumber] > {attempt.AttemptNumber});
                    """).ToListAsync(cancellationToken);
                if (timestamps.Count == 0)
                {
                    await transaction.RollbackAsync(CancellationToken.None);
                    continue;
                }

                attempt.Finish(outcome, timestamps.Single(), errorCode, errorMessage);
                int updatedAttempts = await dbContext.JobAttempts
                    .Where(candidate => candidate.Id == identity.AttemptId && candidate.JobId == identity.JobId && candidate.Outcome == JobAttemptOutcome.Running)
                    .ExecuteUpdateAsync(setters => setters
                        .SetProperty(candidate => candidate.Outcome, attempt.Outcome)
                        .SetProperty(candidate => candidate.FinishedAtUtc, attempt.FinishedAtUtc)
                        .SetProperty(candidate => candidate.DurationMilliseconds, attempt.DurationMilliseconds)
                        .SetProperty(candidate => candidate.ErrorCode, attempt.ErrorCode)
                        .SetProperty(candidate => candidate.ErrorMessage, attempt.ErrorMessage), cancellationToken);
                if (updatedAttempts != 1)
                {
                    throw new DbUpdateConcurrencyException("The execution attempt changed before its transition was persisted.");
                }

                await transaction.CommitAsync(cancellationToken);
                return lookup.Result;
            }
            catch (Exception exception) when (IsTransitionConflict(exception))
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

        return ExecutionResult.Conflicting;
    }

    private async Task<ExecutionLookup> ReadExecutionAsync(ExecutionIdentity identity, CancellationToken cancellationToken)
    {
        Job? job = await dbContext.Jobs.FromSql($"""
            SELECT * FROM [Jobs] WITH (UPDLOCK, HOLDLOCK)
            WHERE [Id] = {identity.JobId} AND [ApplicationId] = {identity.ApplicationId}
            """).AsNoTracking().SingleOrDefaultAsync(cancellationToken);
        if (job is null)
        {
            return new(ExecutionResult.Missing);
        }

        JobAttempt? attempt = await dbContext.JobAttempts.FromSql($"""
            SELECT * FROM [JobAttempts] WITH (UPDLOCK, HOLDLOCK)
            WHERE [Id] = {identity.AttemptId} AND [JobId] = {identity.JobId}
            """).AsNoTracking().SingleOrDefaultAsync(cancellationToken);
        if (attempt is null)
        {
            return new(ExecutionResult.Missing);
        }

        if (attempt.WorkerId != identity.WorkerId)
        {
            return new(ExecutionResult.Conflicting);
        }

        JobExecutionAssignment assignment = new(job, attempt);
        if (attempt.Outcome is JobAttemptOutcome.Failed or JobAttemptOutcome.PermanentlyFailed)
        {
            return new(ExecutionResult.Duplicate, assignment);
        }

        if (await dbContext.JobAttempts.AnyAsync(candidate => candidate.JobId == job.Id && candidate.AttemptNumber > attempt.AttemptNumber, cancellationToken))
        {
            return new(ExecutionResult.Stale);
        }

        if (attempt.Outcome != JobAttemptOutcome.Running)
        {
            ExecutionResult finished = attempt.Outcome switch
            {
                JobAttemptOutcome.Cancelled => ExecutionResult.Cancelled,
                JobAttemptOutcome.TimedOut => ExecutionResult.TimedOut,
                JobAttemptOutcome.Abandoned => ExecutionResult.Stale,
                _ => ExecutionResult.Duplicate
            };
            return new(finished, assignment);
        }

        if (job.Status != JobStatus.Processing || job.OwningWorkerId != identity.WorkerId
            || job.StartedAtUtc != attempt.StartedAtUtc || job.LeaseExpiresAtUtc is null)
        {
            return new(ExecutionResult.Stale);
        }

        if (job.CancellationRequested)
        {
            return new(ExecutionResult.Cancelled, assignment);
        }

        DateTimeOffset now = await GetSqlUtcNowAsync(cancellationToken);
        return new(now >= assignment.DeadlineAtUtc ? ExecutionResult.TimedOut : ExecutionResult.Accepted, assignment);
    }

    private Task<DateTimeOffset> GetSqlUtcNowAsync(CancellationToken cancellationToken) =>
        dbContext.Database.SqlQuery<DateTimeOffset>($"SELECT TODATETIMEOFFSET(SYSUTCDATETIME(), '+00:00') AS [Value]")
            .SingleAsync(cancellationToken);

    private static ExecutionResult ResolveFinishedReport(Job job, JobAttempt attempt, ExecutionReport report, ExecutionResult result)
    {
        if (result != ExecutionResult.Duplicate)
        {
            return result;
        }

        bool duplicate = report switch
        {
            ExecutionReport.Complete complete => attempt.Outcome == JobAttemptOutcome.Succeeded && job.ResultJson == complete.ResultJson,
            ExecutionReport.Fail fail => attempt.Outcome is JobAttemptOutcome.Failed or JobAttemptOutcome.PermanentlyFailed
                && attempt.ErrorCode == fail.ErrorCode && attempt.ErrorMessage == fail.ErrorMessage,
            _ => false
        };
        return duplicate ? ExecutionResult.Duplicate : ExecutionResult.Conflicting;
    }

    private static JobAttemptOutcome ResolveOutcome(ExecutionResult result, ExecutionReport report) => result switch
    {
        ExecutionResult.Cancelled => JobAttemptOutcome.Cancelled,
        ExecutionResult.TimedOut => JobAttemptOutcome.TimedOut,
        _ => report switch
        {
            ExecutionReport.Complete => JobAttemptOutcome.Succeeded,
            ExecutionReport.Fail fail => JobFailurePolicy.IsPermanent(fail.ErrorCode) ? JobAttemptOutcome.PermanentlyFailed : JobAttemptOutcome.Failed,
            _ => throw new InvalidOperationException("The execution report is not valid for an active execution.")
        }
    };

    private static (string? Code, string? Message) ResolveError(Job job, ExecutionReport report, JobAttemptOutcome outcome) => outcome switch
    {
        JobAttemptOutcome.Cancelled => ("CancellationRequested", "Job cancellation was requested."),
        JobAttemptOutcome.TimedOut => ("Timeout", $"Job timed out after {job.TimeoutSeconds} second(s)."),
        _ => report is ExecutionReport.Fail fail ? (fail.ErrorCode, fail.ErrorMessage) : (null, null)
    };

    private static void ApplyJobTransition(Job job, ExecutionReport report, JobAttemptOutcome outcome, string? errorCode, string? errorMessage, TimeSpan retryDelay, DateTimeOffset now)
    {
        string error = $"{errorCode}: {errorMessage}";
        error = error.Length > 4000 ? error[..4000] : error;
        switch (outcome)
        {
            case JobAttemptOutcome.Succeeded:
                job.Complete(((ExecutionReport.Complete)report).ResultJson, now);
                break;
            case JobAttemptOutcome.Cancelled:
                job.Cancel(now);
                break;
            case JobAttemptOutcome.PermanentlyFailed:
                job.DeadLetter(error, now);
                break;
            case JobAttemptOutcome.TimedOut:
                job.TimeOut(now.Add(retryDelay), now);
                break;
            default:
                job.Fail(error, now.Add(retryDelay), now);
                break;
        }
    }

    private static bool IsTransitionConflict(Exception exception) =>
        exception is DbUpdateConcurrencyException
        || exception is SqlException { Number: 1205 }
        || exception is DbUpdateException { InnerException: SqlException { Number: 1205 } };
}
