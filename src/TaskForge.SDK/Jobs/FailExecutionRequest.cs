namespace TaskForge.SDK.Jobs;

public sealed record FailExecutionRequest(string WorkerId, string ErrorCode, string ErrorMessage);
