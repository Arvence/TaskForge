namespace TaskForge.SDK.Jobs;

public sealed record WaitForExecutionRequest(string WorkerId, IReadOnlyList<string> SupportedTypes, int WaitSeconds = 20);
