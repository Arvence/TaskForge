using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using TaskForge.Application.Abstractions.Execution;
using TaskForge.Application.Jobs;
using TaskForge.Domain.Jobs;

namespace TaskForge.Application.Workers;

public sealed class JobExecutor(IJobQueue jobQueue, IEnumerable<IJobHandler> handlers, JobCancellationRegistry cancellationRegistry, TimeProvider timeProvider, IOptions<WorkerOptions> options, JobRetryPolicy retryPolicy, ILogger<JobExecutor> logger)
{
    private readonly IReadOnlyDictionary<string, IJobHandler> _handlers =
        handlers.ToDictionary(handler => handler.JobType, StringComparer.OrdinalIgnoreCase);
    private readonly WorkerOptions _options = options.Value;

    public async Task<bool> ProcessNextAsync(string workerId, Action<Guid?> currentJobChanged, CancellationToken acquisitionToken, CancellationToken executionStoppingToken)
    {
        DateTimeOffset now = timeProvider.GetUtcNow();
        int recoveredJobs = await jobQueue.RecoverExpiredLeasesAsync(now, acquisitionToken);
        if (recoveredJobs > 0)
        {
            logger.LogWarning("Recovered {RecoveredJobCount} job(s) with expired worker leases.", recoveredJobs);
        }

        Job? job = await jobQueue.TryAcquireNextAsync(workerId, TimeSpan.FromSeconds(_options.LeaseGraceSeconds), now, acquisitionToken);
        if (job is null)
        {
            return false;
        }

        currentJobChanged(job.Id);
        using JobCancellationRegistry.Registration cancellation = cancellationRegistry.Register(job.Id);
        try
        {
            if (await jobQueue.IsCancellationRequestedAsync(job.Id, executionStoppingToken))
            {
                cancellationRegistry.Cancel(job.Id);
                await CancelAsync(job.Id);
                return true;
            }

            if (!_handlers.TryGetValue(job.Type, out IJobHandler? handler))
            {
                long version = job.Version;
                job.DeadLetter($"No handler is registered for job type '{job.Type}'.", timeProvider.GetUtcNow());
                await PersistTransitionAsync(job, version, null, executionStoppingToken);
                return true;
            }

            JobAttempt? attempt = await jobQueue.TryStartAttemptAsync(job, timeProvider.GetUtcNow(), executionStoppingToken);
            if (attempt is null)
            {
                return true;
            }

            logger.LogInformation("Worker {WorkerId} started job {JobId} attempt {AttemptNumber}.", workerId, job.Id, attempt.AttemptNumber);
            using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(job.TimeoutSeconds));
            using CancellationTokenSource execution = CancellationTokenSource.CreateLinkedTokenSource(executionStoppingToken, timeout.Token, cancellation.Token);
            string? resultJson = null;
            JobAttemptOutcome outcome;
            string? errorCode = null;
            string? errorMessage = null;

            try
            {
                resultJson = await handler.HandleAsync(job.PayloadJson, execution.Token);
                outcome = JobAttemptOutcome.Succeeded;
            }
            catch (OperationCanceledException) when (cancellation.Token.IsCancellationRequested)
            {
                outcome = JobAttemptOutcome.Cancelled;
                errorCode = "CancellationRequested";
                errorMessage = "Job cancellation was requested.";
            }
            catch (OperationCanceledException) when (executionStoppingToken.IsCancellationRequested)
            {
                outcome = JobAttemptOutcome.Abandoned;
                errorCode = "HostStopping";
                errorMessage = "The worker stopped before the attempt finished.";
            }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested)
            {
                outcome = JobAttemptOutcome.TimedOut;
                errorCode = "Timeout";
                errorMessage = $"Job timed out after {job.TimeoutSeconds} second(s).";
            }
            catch (NonRetryableJobException exception)
            {
                outcome = JobAttemptOutcome.PermanentlyFailed;
                errorCode = exception.GetType().Name;
                errorMessage = exception.Message;
                logger.LogWarning(exception, "Job {JobId} failed permanently in worker {WorkerId}.", job.Id, workerId);
            }
            catch (Exception exception)
            {
                outcome = JobAttemptOutcome.Failed;
                errorCode = exception.GetType().Name;
                errorMessage = exception.Message;
                logger.LogError(exception, "Job {JobId} failed in worker {WorkerId}.", job.Id, workerId);
            }

            DateTimeOffset finishedAt = timeProvider.GetUtcNow();
            if (cancellation.Token.IsCancellationRequested || await jobQueue.IsCancellationRequestedAsync(job.Id, CancellationToken.None))
            {
                await CancelAsync(job.Id, attempt.Id);
                return true;
            }

            attempt.Finish(outcome, finishedAt, errorCode, errorMessage);
            if (outcome == JobAttemptOutcome.Abandoned)
            {
                await jobQueue.FinishAttemptAsync(attempt, CancellationToken.None);
                logger.LogInformation("Worker {WorkerId} stopped while processing job {JobId}; the job will be recovered when its lease expires.", workerId, job.Id);
                return true;
            }

            long expectedVersion = job.Version;
            if (outcome == JobAttemptOutcome.Succeeded)
            {
                job.Complete(resultJson, finishedAt);
            }
            else if (outcome == JobAttemptOutcome.PermanentlyFailed)
            {
                job.DeadLetter(FormatError(attempt), finishedAt);
            }
            else
            {
                job.Fail(FormatError(attempt), finishedAt.Add(retryPolicy.GetDelay(job.RetryCount)), finishedAt);
            }

            bool persisted = await PersistTransitionAsync(job, expectedVersion, attempt, CancellationToken.None);
            if (!persisted)
            {
                if (await jobQueue.IsCancellationRequestedAsync(job.Id, CancellationToken.None))
                {
                    await CancelAsync(job.Id, attempt.Id);
                }
                else
                {
                    await jobQueue.FinishAttemptAsync(attempt, CancellationToken.None);
                }
            }

            return true;
        }
        finally
        {
            currentJobChanged(null);
        }
    }

    private async Task<bool> PersistTransitionAsync(Job job, long expectedVersion, JobAttempt? attempt, CancellationToken cancellationToken)
    {
        if (!await jobQueue.TryUpdateAsync(job, expectedVersion, cancellationToken, attempt))
        {
            logger.LogWarning("Could not persist job {JobId} as {JobStatus} because it was updated concurrently.", job.Id, job.Status);
            return false;
        }

        logger.LogInformation("Job {JobId} is now {JobStatus}.", job.Id, job.Status);
        return true;
    }

    private async Task CancelAsync(Guid jobId, Guid? attemptId = null)
    {
        if (!await jobQueue.TryCancelProcessingAsync(jobId, timeProvider.GetUtcNow(), CancellationToken.None, attemptId))
        {
            logger.LogWarning("Could not mark cancelled job {JobId} as cancelled.", jobId);
        }
    }

    private static string FormatError(JobAttempt attempt)
    {
        string error = $"{attempt.ErrorCode}: {attempt.ErrorMessage}";
        return error.Length <= 4000 ? error : error[..4000];
    }
}
