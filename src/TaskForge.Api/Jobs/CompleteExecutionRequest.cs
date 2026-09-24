using System.Text.Json;

namespace TaskForge.Api.Jobs;

public sealed record CompleteExecutionRequest(string? ApplicationId, string? WorkerId, JsonElement? Result = null);
