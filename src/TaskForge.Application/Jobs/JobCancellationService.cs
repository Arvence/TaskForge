using TaskForge.Application.Abstractions.Persistence;
using TaskForge.Application.Jobs.Models;
using TaskForge.Application.Workers;
using TaskForge.Domain.Jobs;

namespace TaskForge.Application.Jobs;

public sealed class JobCancellationService(
    IJobRepository jobRepository,
    JobCancellationRegistry cancellationRegistry,
    TimeProvider timeProvider)
{
    private const int MaxUpdateAttempts = 3;

    public async Task<JobCancellationResult> RequestAsync(
        Guid jobId,
        CancellationToken cancellationToken = default)
    {
        for (int attempt = 0; attempt < MaxUpdateAttempts; attempt++)
        {
            Job? job = await jobRepository.FindAsync(jobId, cancellationToken);
            if (job is null)
            {
                return new JobCancellationResult(
                    JobCancellationStatus.NotFound,
                    null);
            }

            if (job.Status == JobStatus.Cancelled)
            {
                return new JobCancellationResult(
                    JobCancellationStatus.Accepted,
                    job);
            }

            if (job.Status is JobStatus.Completed or JobStatus.DeadLettered)
            {
                return new JobCancellationResult(
                    JobCancellationStatus.AlreadyFinished,
                    job);
            }

            if (job.CancellationRequested)
            {
                cancellationRegistry.Cancel(job.Id);
                return new JobCancellationResult(
                    JobCancellationStatus.Accepted,
                    job);
            }

            long expectedVersion = job.Version;
            job.RequestCancellation(timeProvider.GetUtcNow());

            if (await jobRepository.TryUpdateAsync(
                    job,
                    expectedVersion,
                    cancellationToken))
            {
                cancellationRegistry.Cancel(job.Id);
                return new JobCancellationResult(
                    JobCancellationStatus.Accepted,
                    job);
            }
        }

        return new JobCancellationResult(
            JobCancellationStatus.ConcurrentUpdate,
            null);
    }
}