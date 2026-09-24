using TaskForge.Application.Jobs.Models;

namespace TaskForge.Application.Abstractions.Persistence;

public interface IExecutionStore
{
    Task<JobCancellationResult> CancelAsync(Guid jobId, CancellationToken cancellationToken = default);

    Task<ExecutionLookup> FindExecutionAsync(ExecutionIdentity identity, CancellationToken cancellationToken = default);

    Task<ExecutionResult> TransitionAsync(ExecutionIdentity identity, ExecutionReport report, CancellationToken cancellationToken = default);
}
