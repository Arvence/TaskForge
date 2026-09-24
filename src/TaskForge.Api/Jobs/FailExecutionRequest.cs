namespace TaskForge.Api.Jobs;

public sealed record FailExecutionRequest(string? ApplicationId, string? WorkerId, string? ErrorCode, string? ErrorMessage);
