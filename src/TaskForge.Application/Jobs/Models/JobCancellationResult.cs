using TaskForge.Domain.Jobs;

namespace TaskForge.Application.Jobs.Models;

public enum JobCancellationStatus
{
    Accepted = 0,
    NotFound = 1,
    AlreadyFinished = 2,
    ConcurrentUpdate = 3
}

public sealed record JobCancellationResult(
    JobCancellationStatus Status,
    Job? Job);