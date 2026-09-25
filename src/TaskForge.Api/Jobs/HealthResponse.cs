namespace TaskForge.Api.Jobs;

public sealed record HealthResponse(string Status, string Service, DateTimeOffset TimestampUtc);

public sealed record ReadinessResponse(string Status);
