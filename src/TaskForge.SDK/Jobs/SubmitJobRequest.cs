using System.Text.Json;

namespace TaskForge.SDK.Jobs;

public sealed record SubmitJobRequest(string Type, JsonElement Payload, JobPriority Priority = JobPriority.Normal, int MaxRetries = 3, int TimeoutSeconds = 30);
