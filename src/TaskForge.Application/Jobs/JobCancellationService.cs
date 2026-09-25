using TaskForge.Application.Abstractions.Persistence;
using TaskForge.Application.Jobs.Models;

namespace TaskForge.Application.Jobs;

public sealed class JobCancellationService(IExecutionStore executionStore)
{
    public Task<JobCancellationResult> RequestAsync(Guid jobId, CancellationToken cancellationToken = default) =>
        executionStore.CancelAsync(jobId, cancellationToken);
}
