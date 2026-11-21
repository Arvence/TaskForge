using System.Text.Json;
using System.Text.Json.Serialization;

using TaskForge.SDK.Handlers;

namespace TaskForge.Cli;

internal static class JobRouter
{
    internal static readonly JsonSerializerOptions ParameterOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        RespectNullableAnnotations = true,
        RespectRequiredConstructorParameters = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        AllowDuplicateProperties = false
    };

    internal static (string Operation, JsonElement Parameters) SelectOperation(JsonElement payload)
    {
        if (payload.ValueKind != JsonValueKind.Object ||
            !payload.TryGetProperty("operation", out JsonElement selector) || selector.ValueKind != JsonValueKind.String ||
            selector.GetString() is not { } operation || string.IsNullOrWhiteSpace(operation))
        {
            throw new NonRetryableJobException("The payload must be an object with an operation string.");
        }

        Dictionary<string, JsonElement> parameters = new(StringComparer.Ordinal);
        foreach (JsonProperty property in payload.EnumerateObject())
        {
            if (!parameters.TryAdd(property.Name, property.Value))
            {
                throw new NonRetryableJobException("Operation payload fields must be unique.");
            }
        }

        parameters.Remove("operation");
        return (operation, JsonSerializer.SerializeToElement(parameters));
    }
}
