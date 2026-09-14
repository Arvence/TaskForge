namespace TaskForge.Application.Jobs.Models;

public sealed record JobStatusCounts(long Pending = 0, long Queued = 0, long Processing = 0, long Retrying = 0, long Completed = 0, long DeadLettered = 0, long Cancelled = 0);
