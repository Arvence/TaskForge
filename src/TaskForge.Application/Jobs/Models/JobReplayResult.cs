using TaskForge.Domain.Jobs;

namespace TaskForge.Application.Jobs.Models;

public enum JobReplayStatus
{
    Created = 0,
    NotFound = 1,
    InvalidState = 2
}

public sealed record JobReplayResult(JobReplayStatus Status, Job? Job);
