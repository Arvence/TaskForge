using System.Text.Json;
using TaskForge.Application.Jobs.Models;

namespace TaskForge.Application.Jobs.Validation;

public sealed class SubmitJobValidator
{
    public IReadOnlyDictionary<string, string[]> Validate(SubmitJobCommand command)
    {
        Dictionary<string, string[]> errors = [];

        if (string.IsNullOrWhiteSpace(command.Type))
        {
            errors["Type"] = ["Job type is required."];
        }
        else if (command.Type.Trim().Length > 100)
        {
            errors["Type"] = ["Job type cannot exceed 100 characters."];
        }

        if (!HasPayload(command.PayloadJson))
        {
            errors["Payload"] = ["Job payload is required."];
        }

        if (!Enum.IsDefined(command.Priority))
        {
            errors["Priority"] = ["Job priority is invalid."];
        }

        if (command.MaxRetries is < 0 or > 10)
        {
            errors["MaxRetries"] = ["Max retries must be between 0 and 10."];
        }

        if (command.TimeoutSeconds is < 1 or > 3600)
        {
            errors["TimeoutSeconds"] = ["Timeout seconds must be between 1 and 3600."];
        }

        return errors;
    }

    private static bool HasPayload(string payloadJson)
    {
        if (string.IsNullOrWhiteSpace(payloadJson))
        {
            return false;
        }

        try
        {
            using JsonDocument payload = JsonDocument.Parse(payloadJson);
            return payload.RootElement.ValueKind is not JsonValueKind.Null;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
