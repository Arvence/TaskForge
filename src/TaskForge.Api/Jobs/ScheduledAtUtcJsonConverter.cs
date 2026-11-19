using System.Text.Json;
using System.Text.Json.Serialization;

namespace TaskForge.Api.Jobs;

internal sealed class ScheduledAtUtcJsonConverter : JsonConverter<DateTimeOffset?>
{
    public override DateTimeOffset? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String && reader.TryGetDateTimeOffset(out DateTimeOffset value))
        {
            string text = reader.GetString()!;
            if (text.Contains('T') && (text.EndsWith('Z') || (text.Length >= 6 && text[^6] is '+' or '-')))
            {
                return value.ToUniversalTime();
            }
        }

        throw new JsonException("scheduledAtUtc must be an ISO 8601 timestamp with an explicit UTC offset.");
    }

    public override void Write(Utf8JsonWriter writer, DateTimeOffset? value, JsonSerializerOptions options)
    {
        if (value is { } scheduledAt)
        {
            writer.WriteStringValue(scheduledAt);
        }
        else
        {
            writer.WriteNullValue();
        }
    }
}
