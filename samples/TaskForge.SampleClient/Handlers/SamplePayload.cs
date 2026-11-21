using System.Text.Json;
using System.Text.Json.Serialization;

using TaskForge.SDK.Handlers;

namespace TaskForge.SampleClient.Handlers;

internal static class SamplePayload
{
    private static readonly JsonSerializerOptions BindingOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        AllowDuplicateProperties = false
    };

    public static T Bind<T>(JsonElement payload, string jobType) where T : class
    {
        try
        {
            return payload.Deserialize<T>(BindingOptions) ?? throw new JsonException("A non-null sample payload is required.");
        }
        catch (JsonException exception)
        {
            throw new InvalidJobPayloadException(jobType, typeof(T), exception);
        }
    }
}
