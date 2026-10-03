using System.Text.Json;

namespace TaskForge.SampleClient.Handlers;

public sealed record HttpRequestPayload(string? Url = null, string? Method = null, JsonElement? Body = null, IReadOnlyDictionary<string, string?>? Headers = null);
