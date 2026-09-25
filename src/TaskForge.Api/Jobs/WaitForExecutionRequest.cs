using TaskForge.Application.Jobs;

namespace TaskForge.Api.Jobs;

public sealed record WaitForExecutionRequest(string? ApplicationId, string? WorkerId, string?[]? SupportedTypes, int WaitSeconds = JobDistributionService.DefaultWaitSeconds);
