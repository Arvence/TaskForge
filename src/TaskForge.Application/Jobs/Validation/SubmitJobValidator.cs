using System.Text;
using System.Text.Json;

using TaskForge.Application.Jobs.Models;
using TaskForge.Domain.Jobs;

namespace TaskForge.Application.Jobs.Validation;

public sealed class SubmitJobValidator
{
    private const int MaxPayloadBytes = 16 * 1024;
    private static readonly JsonDocumentOptions PayloadOptions = new() { MaxDepth = 64, AllowDuplicateProperties = false };

    public IReadOnlyDictionary<string, string[]> Validate(SubmitJobCommand command, string? idempotencyKey = null)
    {
        ArgumentNullException.ThrowIfNull(command);

        Dictionary<string, string[]> errors = [];

        AddError(errors, "ApplicationId", JobApplicationId.IsValid(command.ApplicationId)
            ? null
            : "Application ID must contain 1 to 100 ASCII letters, digits, dots, underscores, or hyphens.");
        AddError(errors, "Type", ValidateType(command.Type));
        AddError(errors, "Payload", ValidatePayload(command.PayloadJson));
        AddError(errors, "Priority", ValidatePriority(command.Priority));
        AddError(errors, "MaxRetries", ValidateMaxRetries(command.MaxRetries));
        AddError(errors, "TimeoutSeconds", ValidateTimeoutSeconds(command.TimeoutSeconds));
        AddError(errors, "Idempotency-Key", ValidateIdempotencyKey(idempotencyKey));

        return errors;
    }

    private static void AddError(IDictionary<string, string[]> errors, string field, string? error)
    {
        if (error is not null)
        {
            errors[field] = [error];
        }
    }

    private static string? ValidateType(string type)
    {
        if (string.IsNullOrWhiteSpace(type))
        {
            return "Job type is required.";
        }

        return type.Trim().Length > 100
            ? "Job type cannot exceed 100 characters."
            : null;
    }

    private static string? ValidatePayload(string payloadJson)
    {
        if (string.IsNullOrWhiteSpace(payloadJson))
        {
            return "Job payload is required.";
        }

        if (Encoding.UTF8.GetByteCount(payloadJson) > MaxPayloadBytes)
        {
            return $"Payload must not exceed {MaxPayloadBytes} UTF-8 bytes.";
        }

        try
        {
            using JsonDocument payload = JsonDocument.Parse(payloadJson, PayloadOptions);
            return null;
        }
        catch (JsonException)
        {
            return "Job payload must be valid JSON within the nesting depth limit and without duplicate object keys.";
        }
    }

    private static string? ValidatePriority(JobPriority priority) => Enum.IsDefined(priority) ? null : "Job priority is invalid.";
    private static string? ValidateMaxRetries(int maxRetries) => maxRetries is < 0 or > 10 ? "Max retries must be between 0 and 10." : null;
    private static string? ValidateTimeoutSeconds(int timeoutSeconds) => timeoutSeconds is < 1 or > 3600 ? "Timeout seconds must be between 1 and 3600." : null;

    private static string? ValidateIdempotencyKey(string? idempotencyKey)
    {
        if (idempotencyKey is null)
        {
            return null;
        }

        return string.IsNullOrWhiteSpace(idempotencyKey)
            || idempotencyKey.Trim().Length > 100
                ? "Idempotency key must contain between 1 and 100 characters."
                : null;
    }
}
