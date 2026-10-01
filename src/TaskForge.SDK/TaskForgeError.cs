using System.Text.Json;
using System.Text.Json.Serialization;

namespace TaskForge.SDK;

public sealed record TaskForgeError
{
    public string? Code { get; init; }
    public string? Type { get; init; }
    public string? Title { get; init; }
    public string? Detail { get; init; }
    public string? Message { get; init; }
    public string? TraceId { get; init; }
    public Dictionary<string, string[]> Errors { get; init; } = [];

    [JsonExtensionData]
    public Dictionary<string, JsonElement> AdditionalProperties { get; init; } = [];
}
