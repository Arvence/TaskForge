using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using TaskForge.Application.Abstractions.Execution;
using TaskForge.Domain.Jobs;

namespace TaskForge.Application.Workers;

public sealed class JobExecutor(
    IJobQueue jobQueue,
    IEnumerable<IJobHandler> handlers,
    JobCancellationRegistry cancellationRegistry,
    TimeProvider timeProvider,
    IOptions<WorkerOptions> options,
    ILogger<JobExecutor> logger)
{
    private const int MaxErrorLength = 4000;
    private readonly IReadOnlyDictionary<string, IJobHandler> _handlers =
        handlers.ToDictionary(
            handler => handler.JobType,
            StringComparer.OrdinalIgnoreCase);
    private readonly WorkerOptions _options = options.Value;

    public async Task<bool> ProcessNextAsync(
        string workerId,
        Action<Guid?> currentJobChanged,
        CancellationToken acquisitionToken,
        CancellationToken executionStoppingToken)
    {
        DateTimeOffset now = timeProvider.GetUtcNow();
        int recoveredJobs = await jobQueue.RecoverExpiredLeasesAsync(
            now,
            acquisitionToken);

        if (recoveredJobs > 0)
        {
            logger.LogWarning(
                "Recovered {RecoveredJobCount} job(s) with expired worker leases.",
                recoveredJobs);
        }

        Job? job = await jobQueue.TryAcquireNextAsync(
            workerId,
            TimeSpan.FromSeconds(_options.LeaseGraceSeconds),
            now,
            acquisitionToken);

        if (job is null)
        {
            return false;
        }

        currentJobChanged(job.Id);
        using JobCancellationRegistry.Registration cancellation =
            cancellationRegistry.Register(job.Id);

        try
        {
            if (await jobQueue.IsCancellationRequestedAsync(
                    job.Id,
                    executionStoppingToken))
            {
                cancellationRegistry.Cancel(job.Id);
                await CancelAsync(job.Id);
                return true;
            }

            logger.LogInformation(
                "Worker {WorkerId} started job {JobId} of type {JobType}.",
                workerId,
                job.Id,
                job.Type);

            if (!_handlers.TryGetValue(job.Type, out IJobHandler? handler))
            {
                await DeadLetterAsync(
                    job,
                    $"No handler is registered for job type '{job.Type}'.",
                    executionStoppingToken);
                return true;
            }

            using CancellationTokenSource timeout = new(
                TimeSpan.FromSeconds(job.TimeoutSeconds));
            using CancellationTokenSource execution = CancellationTokenSource
                .CreateLinkedTokenSource(
                    executionStoppingToken,
                    timeout.Token,
                    cancellation.Token);

            try
            {
                string? resultJson = await handler.HandleAsync(
                    job.PayloadJson,
                    execution.Token);

                DateTimeOffset completedAt = timeProvider.GetUtcNow();
                if (cancellation.Token.IsCancellationRequested
                    || await jobQueue.IsCancellationRequestedAsync(
                        job.Id,
                        executionStoppingToken))
                {
                    await CancelAsync(job.Id);
                    return true;
                }

                long expectedVersion = job.Version;
                job.Complete(resultJson, completedAt);

                bool completed = await PersistTransitionAsync(
                    job,
                    expectedVersion,
                    "complete",
                    executionStoppingToken);

                if (!completed
                    && await jobQueue.IsCancellationRequestedAsync(
                        job.Id,
                        CancellationToken.None))
                {
                    await CancelAsync(job.Id);
                }
            }
            catch (OperationCanceledException)
                when (cancellation.Token.IsCancellationRequested)
            {
                await CancelAsync(job.Id);
            }
            catch (OperationCanceledException)
                when (executionStoppingToken.IsCancellationRequested)
            {
                logger.LogInformation(
                    "Worker {WorkerId} stopped while processing job {JobId}; "
                    + "the job will be recovered when its lease expires.",
                    workerId,
                    job.Id);
            }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested)
            {
                await FailAsync(
                    job,
                    $"Job timed out after {job.TimeoutSeconds} second(s).",
                    CancellationToken.None);
            }
            catch (Exception exception)
            {
                logger.LogError(
                    exception,
                    "Job {JobId} failed in worker {WorkerId}.",
                    job.Id,
                    workerId);

                await FailAsync(
                    job,
                    $"{exception.GetType().Name}: {exception.Message}",
                    executionStoppingToken);
            }

            return true;
        }
        finally
        {
            currentJobChanged(null);
        }
    }

    private async Task FailAsync(
        Job job,
        string error,
        CancellationToken cancellationToken)
    {
        DateTimeOffset failedAt = timeProvider.GetUtcNow();
        long expectedVersion = job.Version;
        job.Fail(
            Truncate(error),
            failedAt.AddSeconds(_options.RetryDelaySeconds),
            failedAt);

        await PersistTransitionAsync(
            job,
            expectedVersion,
            job.Status == JobStatus.Retrying ? "schedule for retry" : "dead-letter",
            cancellationToken);
    }

    private async Task DeadLetterAsync(
        Job job,
        string error,
        CancellationToken cancellationToken)
    {
        long expectedVersion = job.Version;
        job.DeadLetter(Truncate(error), timeProvider.GetUtcNow());
        await PersistTransitionAsync(
            job,
            expectedVersion,
            "dead-letter",
            cancellationToken);
    }

    private async Task<bool> PersistTransitionAsync(
        Job job,
        long expectedVersion,
        string transition,
        CancellationToken cancellationToken)
    {
        if (!await jobQueue.TryUpdateAsync(
                job,
                expectedVersion,
                cancellationToken))
        {
            logger.LogWarning(
                "Could not {Transition} job {JobId} because it was updated concurrently.",
                transition,
                job.Id);
            return false;
        }

        logger.LogInformation(
            "Job {JobId} is now {JobStatus}.",
            job.Id,
            job.Status);
        return true;
    }

    private async Task CancelAsync(Guid jobId)
    {
        if (!await jobQueue.TryCancelProcessingAsync(
                jobId,
                timeProvider.GetUtcNow(),
                CancellationToken.None))
        {
            logger.LogWarning(
                "Could not mark cancelled job {JobId} as cancelled.",
                jobId);
        }
    }

    private static string Truncate(string value) =>
        value.Length <= MaxErrorLength
            ? value
            : value[..MaxErrorLength];
}