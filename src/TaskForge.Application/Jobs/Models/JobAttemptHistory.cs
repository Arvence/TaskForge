using TaskForge.Domain.Jobs;

namespace TaskForge.Application.Jobs.Models;

public sealed record JobAttemptHistory(int TimeoutSeconds, IReadOnlyList<JobAttempt> Attempts);
