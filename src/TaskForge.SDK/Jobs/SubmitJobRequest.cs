using System.Text.Json;
using System.Text.Json.Serialization;

namespace TaskForge.SDK.Jobs;

[method: JsonConstructor]
public sealed record SubmitJobRequest(string Type, JsonElement Payload, JobPriority Priority = JobPriority.Normal, int MaxRetries = 3, int TimeoutSeconds = 30)
{
    public SubmitJobRequest(JsonElement payload) : this("general", payload)
    {
    }

    public DateTimeOffset? ScheduledAtUtc { get; init; }
}
