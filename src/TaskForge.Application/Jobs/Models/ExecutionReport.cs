using System.Text.Json;

using TaskForge.Application.Common.Exceptions;

namespace TaskForge.Application.Jobs.Models;

public abstract record ExecutionReport
{
    private ExecutionReport() { }

    public sealed record Complete : ExecutionReport
    {
        public Complete(string? resultJson = null)
        {
            if (resultJson is null)
            {
                return;
            }

            try
            {
                using JsonDocument document = JsonDocument.Parse(resultJson);
                ResultJson = document.RootElement.ValueKind == JsonValueKind.Null
                    ? null : JsonSerializer.Serialize(document.RootElement);
            }
            catch (JsonException)
            {
                throw InvalidResult("Result must be valid JSON or null.");
            }

            if (ResultJson is { Length: > 4000 })
            {
                throw InvalidResult("Serialized result cannot exceed 4000 characters.");
            }
        }

        public string? ResultJson { get; }

        private static ApplicationValidationException InvalidResult(string message) =>
            new(new Dictionary<string, string[]> { ["Result"] = [message] });
    }

    public sealed record Fail : ExecutionReport
    {
        public Fail(string? errorCode, string? errorMessage)
        {
            ErrorCode = errorCode?.Trim().ToLowerInvariant() ?? string.Empty;
            ErrorMessage = errorMessage?.Trim() ?? string.Empty;
            Dictionary<string, string[]> errors = [];
            if (ErrorCode.Length is < 1 or > 100)
            {
                errors["ErrorCode"] = ["Error code must contain 1 to 100 characters after trimming."];
            }

            if (ErrorMessage.Length is < 1 or > 4000)
            {
                errors["ErrorMessage"] = ["Error message must contain 1 to 4000 characters after trimming."];
            }

            if (errors.Count > 0)
            {
                throw new ApplicationValidationException(errors);
            }
        }

        public string ErrorCode { get; }
        public string ErrorMessage { get; }
    }

    public sealed record Timeout : ExecutionReport;

    public sealed record Cancel : ExecutionReport;
}
