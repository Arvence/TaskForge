using TaskForge.Domain.Jobs;

namespace TaskForge.Application.Jobs.Models;

public sealed record SubmitJobCommand(
    string Type,
    string PayloadJson,
    JobPriority Priority,
    int MaxRetries,
    int TimeoutSeconds
);
