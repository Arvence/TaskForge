namespace TaskForge.Debugging.Api;

internal sealed record HealthResponse(
    string Status,
    string Service,
    DateTimeOffset TimestampUtc);

internal sealed record JobSummary(
    Guid Id,
    string Type,
    string Priority,
    string Status,
    int MaxRetries,
    int RetryCount,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc);

internal sealed record JobPageResponse(
    IReadOnlyList<JobSummary> Items,
    int Page,
    int PageSize,
    int TotalCount,
    int TotalPages);

internal sealed record WorkerManagerSnapshot(
    int DesiredWorkerCount,
    int ActiveWorkerCount,
    int MaximumWorkerCount,
    IReadOnlyList<WorkerSnapshot> Workers);

internal sealed record WorkerSnapshot(
    string Id,
    string Status,
    Guid? CurrentJobId,
    DateTimeOffset StartedAtUtc);
