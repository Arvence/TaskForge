namespace TaskForge.Debugging.Api;

internal sealed record HealthResponse(string Status, string Service, DateTimeOffset TimestampUtc);

internal sealed record JobSummary(Guid Id, string ApplicationId, string Type, string Priority, string Status, int MaxRetries, int RetryCount, DateTimeOffset CreatedAtUtc, DateTimeOffset UpdatedAtUtc);

internal sealed record JobPageResponse(IReadOnlyList<JobSummary> Items, int Page, int PageSize, int TotalCount, int TotalPages);

internal sealed record JobStatisticsResponse(JobStatusCounts CountsByStatus, long TotalJobs, decimal? SuccessRatePercent);

internal sealed record JobStatusCounts(long Pending, long Queued, long Processing, long Retrying, long Completed, long DeadLettered, long Cancelled);
