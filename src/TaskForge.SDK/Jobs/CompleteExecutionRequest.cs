using System.Text.Json;

namespace TaskForge.SDK.Jobs;

public sealed record CompleteExecutionRequest(string WorkerId, JsonElement? Result = null);
