using TaskForge.Application.Abstractions.Persistence;
using TaskForge.Application.Jobs.Models;
using TaskForge.Application.Workers;

namespace TaskForge.Application.Jobs;

public sealed class JobCancellationService(IExecutionStore executionStore, JobCancellationRegistry cancellationRegistry)
{
    public async Task<JobCancellationResult> RequestAsync(Guid jobId, CancellationToken cancellationToken = default)
    {
        JobCancellationResult result = await executionStore.CancelAsync(jobId, cancellationToken);
        if (result.Status == JobCancellationStatus.Accepted)
        {
            cancellationRegistry.Cancel(jobId);
        }

        return result;
    }
}
