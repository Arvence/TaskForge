using System.Diagnostics.CodeAnalysis;
using System.Text.Json;

using TaskForge.Application.Jobs.Validation;
using TaskForge.Domain.Jobs;

namespace TaskForge.Api.Jobs;

internal sealed class JobPayloadPolicy : IJobPayloadPolicy
{
    private const int MaxPayloadBytes = 16 * 1024;
    private readonly Dictionary<string, Dictionary<string, Action<JsonElement>>> _applications = new(StringComparer.Ordinal);

    public JobPayloadPolicy(IConfiguration configuration)
    {
        foreach (IConfigurationSection application in configuration.GetSection("JobAccess:Applications").GetChildren())
        {
            Dictionary<string, Action<JsonElement>> jobs = new(StringComparer.OrdinalIgnoreCase);
            foreach (IConfigurationSection job in application.GetSection("Jobs").GetChildren())
            {
                Action<JsonElement> validate = job["Contract"] switch
                {
                    "generate-report" => ValidateReport,
                    "calculation" => ValidateCalculation,
                    "http-get" => BindTarget(job, application.Key, ValidateHttpGet, originOnly: true),
                    "wowiki-report" => BindTarget(job, application.Key, ValidateWoWikiReport),
                    _ => throw new InvalidOperationException($"Unsupported payload contract for '{application.Key}/{job.Key}'.")
                };
                jobs.Add(job.Key.Trim(), validate);
            }

            _applications.Add(JobApplicationId.Normalize(application.Key), jobs);
        }
    }

    public string? Validate(string applicationId, string jobType, JsonElement payload, int payloadBytes)
    {
        if (!_applications.TryGetValue(applicationId, out var jobs) || !jobs.TryGetValue(jobType, out var validate))
        {
            return "No payload contract is configured for this application and job type.";
        }

        if (payloadBytes > MaxPayloadBytes)
        {
            return $"Payload must not exceed {MaxPayloadBytes} UTF-8 bytes.";
        }

        try
        {
            validate(payload);
            return null;
        }
        catch (PayloadException exception)
        {
            return exception.Message;
        }
    }

    private static Action<JsonElement> BindTarget(IConfigurationSection job, string applicationId, Action<JsonElement, Uri> validate, bool originOnly = false)
    {
        if (!TryHttpUrl(job["Target"], out Uri? target) || (originOnly && target.AbsolutePath != "/"))
        {
            throw new InvalidOperationException($"Configure a valid HTTP target for '{applicationId}/{job.Key}'.");
        }

        return payload => validate(payload, target);
    }

    private static void ValidateCalculation(JsonElement payload)
    {
        RequireObject(payload, "payload", "values");
        foreach (JsonElement value in RequireArray(payload, "values", "payload.values", 1000))
        {
            if (value.ValueKind != JsonValueKind.Number || !value.TryGetDecimal(out _))
            {
                throw new PayloadException("payload.values[] must be a decimal number.");
            }
        }
    }

    private static void ValidateReport(JsonElement payload)
    {
        RequireObject(payload, "payload", "title", "entries");
        RequireText(payload, "title", "payload.title", 120);
        foreach (JsonElement entry in RequireArray(payload, "entries", "payload.entries", 1000))
        {
            RequireObject(entry, "payload.entries[]", "category", "amount");
            RequireText(entry, "category", "payload.entries[].category", 80);
            if (!entry.TryGetProperty("amount", out JsonElement amount) || amount.ValueKind != JsonValueKind.Number || !amount.TryGetDecimal(out decimal number)
                || number is < 0 or > 1_000_000_000_000m || decimal.Round(number, 2) != number)
            {
                throw new PayloadException("payload.entries[].amount must be between 0 and 1000000000000 with at most two decimal places.");
            }
        }
    }

    private static void ValidateHttpGet(JsonElement payload, Uri target)
    {
        RequireObject(payload, "payload", "url", "method", "body", "headers");
        string url = RequireText(payload, "url", "payload.url", 2048);
        if (!TryHttpUrl(url, out Uri? requested) || requested.GetLeftPart(UriPartial.Authority) != target.GetLeftPart(UriPartial.Authority))
        {
            throw new PayloadException("payload.url must use the configured HTTP origin and contain no credentials, query, or fragment.");
        }

        if (RequireText(payload, "method", "payload.method", 4) != "GET")
        {
            throw new PayloadException("payload.method must be GET.");
        }

        foreach (string property in new[] { "body", "headers" })
        {
            if (payload.TryGetProperty(property, out JsonElement value) && value.ValueKind != JsonValueKind.Null)
            {
                throw new PayloadException($"payload.{property} must be omitted or null for this job type.");
            }
        }
    }

    private static void ValidateWoWikiReport(JsonElement payload, Uri target)
    {
        RequireObject(payload, "payload", "url", "method", "body", "headers");
        if (!payload.TryGetProperty("body", out JsonElement body) || !payload.TryGetProperty("headers", out JsonElement headers))
        {
            throw new PayloadException("payload.body and payload.headers are required.");
        }

        RequireObject(body, "payload.body", "reportId", "externalReference");
        RequireObject(headers, "payload.headers", "X-TaskForge-Source");
        string id = RequireText(body, "reportId", "payload.body.reportId", 20);
        if (id[0] == '0' || !id.All(char.IsAsciiDigit))
        {
            throw new PayloadException("payload.body.reportId must be a positive numeric identifier.");
        }

        if (RequireText(body, "externalReference", "payload.body.externalReference", 24) != $"WOW-{id}")
        {
            throw new PayloadException("payload.body.externalReference must match the report ID.");
        }

        string expectedUrl = $"{target.AbsoluteUri.TrimEnd('/')}/api/internal/taskforge/user-reports/{id}/ready";
        if (RequireText(payload, "url", "payload.url", 2048) != expectedUrl)
        {
            throw new PayloadException("payload.url must match the configured private callback for the report.");
        }

        if (RequireText(payload, "method", "payload.method", 4) != "POST" ||
            RequireText(headers, "X-TaskForge-Source", "payload.headers.X-TaskForge-Source", 32) != "wowiki-user-reports")
        {
            throw new PayloadException("The WoWiki callback requires POST and the configured source marker.");
        }
    }

    private static void RequireObject(JsonElement value, string path, params string[] allowed)
    {
        if (value.ValueKind != JsonValueKind.Object)
        {
            throw new PayloadException($"{path} must be a JSON object.");
        }

        HashSet<string> seen = new(StringComparer.Ordinal);
        foreach (JsonProperty property in value.EnumerateObject())
        {
            if (!allowed.Contains(property.Name, StringComparer.Ordinal))
            {
                throw new PayloadException($"{path} contains a field that is not allowed. Allowed fields: {string.Join(", ", allowed)}.");
            }

            if (!seen.Add(property.Name))
            {
                throw new PayloadException($"{path} contains duplicate fields.");
            }
        }
    }

    private static string RequireText(JsonElement value, string name, string path, int maxLength)
    {
        if (!value.TryGetProperty(name, out JsonElement property) || property.ValueKind != JsonValueKind.String ||
            property.GetString() is not { } text || string.IsNullOrWhiteSpace(text) || text.Length > maxLength)
        {
            throw new PayloadException($"{path} must be a nonblank string of at most {maxLength} characters.");
        }

        return text;
    }

    private static JsonElement.ArrayEnumerator RequireArray(JsonElement value, string name, string path, int maxLength)
    {
        if (!value.TryGetProperty(name, out JsonElement property) || property.ValueKind != JsonValueKind.Array ||
            property.GetArrayLength() == 0 || property.GetArrayLength() > maxLength)
        {
            throw new PayloadException($"{path} must be an array of 1 to {maxLength} items.");
        }

        return property.EnumerateArray();
    }

    private static bool TryHttpUrl(string? value, [NotNullWhen(true)] out Uri? uri) =>
        Uri.TryCreate(value, UriKind.Absolute, out uri) && uri.Scheme is "http" or "https"
        && uri.UserInfo.Length == 0 && uri.Query.Length == 0 && uri.Fragment.Length == 0;

    private sealed class PayloadException(string message) : Exception(message);
}
