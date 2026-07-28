using TaskForge.Domain.Workers;

namespace TaskForge.Application.Workers;

public sealed record WorkerManagerSnapshot(
    int DesiredWorkerCount,
    int ActiveWorkerCount,
    int MaximumWorkerCount,
    IReadOnlyList<WorkerSnapshot> Workers);

public sealed record WorkerSnapshot(
    string Id,
    WorkerStatus Status,
    Guid? CurrentJobId,
    DateTimeOffset StartedAtUtc);