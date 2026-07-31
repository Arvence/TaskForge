using System.Text.Json;

using TaskForge.Application.Abstractions.Execution;
using TaskForge.Application.Jobs.Models;
using TaskForge.Domain.Jobs;

namespace TaskForge.Application.Jobs.Validation;

public sealed class SubmitJobValidator(IEnumerable<IJobHandler> handlers)
{
    private readonly IReadOnlyDictionary<string, IJobHandler> _handlers =
        handlers.ToDictionary(
            handler => handler.JobType,
            StringComparer.OrdinalIgnoreCase);

    public IReadOnlyDictionary<string, string[]> Validate(SubmitJobCommand command, string? idempotencyKey = null)
    {
        ArgumentNullException.ThrowIfNull(command);

        Dictionary<string, string[]> errors = [];

        AddError(errors, "Type", ValidateType(command.Type));
        AddError(errors, "Payload", ValidateJsonPayload(command.PayloadJson));
        ValidateHandlerPayload(errors, command.Type, command.PayloadJson);
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

    private void ValidateHandlerPayload(
        IDictionary<string, string[]> errors,
        string type,
        string payloadJson)
    {
        if (errors.ContainsKey("Type") || errors.ContainsKey("Payload"))
        {
            return;
        }

        string normalizedType = type.Trim();
        if (!_handlers.TryGetValue(normalizedType, out IJobHandler? handler))
        {
            AddError(errors, "Type", $"Job type '{normalizedType}' is not supported.");
            return;
        }

        AddError(errors, "Payload", handler.ValidatePayload(payloadJson));
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

    private static string? ValidateJsonPayload(string payloadJson)
    {
        if (string.IsNullOrWhiteSpace(payloadJson))
        {
            return "Job payload is required.";
        }

        try
        {
            using JsonDocument payload = JsonDocument.Parse(payloadJson);
            return payload.RootElement.ValueKind is JsonValueKind.Null
                ? "Job payload is required."
                : null;
        }
        catch (JsonException)
        {
            return "Job payload is required.";
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