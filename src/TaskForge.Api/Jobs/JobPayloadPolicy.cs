using System.Text.Json;
using System.Text.Json.Serialization;

using TaskForge.Application.Jobs.Validation;
using TaskForge.Domain.Jobs;

namespace TaskForge.Api.Jobs;

internal sealed class JobPayloadPolicy : IJobPayloadPolicy
{
    private const int MaxPayloadBytes = 16 * 1024;
    private static readonly JsonSerializerOptions ContractJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        AllowDuplicateProperties = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };
    private readonly Dictionary<string, Dictionary<string, FieldRule>> _applications = new(StringComparer.Ordinal);

    public JobPayloadPolicy(IConfiguration configuration)
    {
        foreach (IConfigurationSection application in configuration.GetSection("JobAccess:Applications").GetChildren())
        {
            Dictionary<string, FieldRule> jobs = new(StringComparer.OrdinalIgnoreCase);
            foreach (IConfigurationSection job in application.GetSection("Jobs").GetChildren())
            {
                string jobType = job.Key.Trim();
                if (jobType.Length is 0 or > 100 || jobs.ContainsKey(jobType))
                {
                    throw new InvalidOperationException($"Invalid or duplicate job type for application '{application.Key}'.");
                }

                jobs.Add(jobType, ReadContract(job));
            }

            _applications.Add(JobApplicationId.Normalize(application.Key), jobs);
        }
    }

    public string? Validate(string applicationId, string jobType, JsonElement payload, int payloadBytes)
    {
        if (!_applications.TryGetValue(applicationId, out var jobs) || !jobs.TryGetValue(jobType, out FieldRule? contract))
        {
            return "No payload contract is configured for this application and job type.";
        }

        if (payloadBytes > MaxPayloadBytes)
        {
            return $"Payload must not exceed {MaxPayloadBytes} UTF-8 bytes.";
        }

        return ValidateValue(contract, payload, "payload");
    }

    private static FieldRule ReadContract(IConfigurationSection job)
    {
        if (job["Contract"] is not { } json || job.GetChildren().Any(setting => !setting.Key.Equals("Contract", StringComparison.OrdinalIgnoreCase))
            || job.GetSection("Contract").GetChildren().Any())
        {
            throw new InvalidOperationException($"Configure a JSON payload contract as the only job setting for '{job.Path}'.");
        }

        ObjectContract? declaration;
        try
        {
            declaration = JsonSerializer.Deserialize<ObjectContract>(json, ContractJson);
        }
        catch (JsonException)
        {
            throw new InvalidOperationException($"Invalid JSON payload contract for '{job.Path}'. Replace legacy profile names with a JSON object.");
        }

        FieldRule root = new() { Type = "object", Properties = declaration?.Properties };
        if (ValidateRule(root, "properties") is { } error)
        {
            throw new InvalidOperationException($"Invalid payload contract for '{job.Path}'. {error}");
        }

        return root;
    }

    private static string? ValidateRule(FieldRule rule, string path)
    {
        if (rule.Type is not ("string" or "number" or "boolean" or "object" or "array" or "null"))
        {
            return $"{path} must declare a supported type.";
        }

        if ((rule.Properties is not null) != (rule.Type == "object") ||
            (rule.Items is not null) != (rule.Type == "array") ||
            (rule.Type != "string" && (rule.MinLength is not null || rule.MaxLength is not null || rule.NonBlank || rule.AllowedValues is not null)) ||
            (rule.Type != "number" && (rule.Minimum is not null || rule.Maximum is not null || rule.MaxDecimalPlaces is not null)) ||
            (rule.Type != "array" && (rule.MinItems is not null || rule.MaxItems is not null)))
        {
            return $"{path} must declare only settings supported by its type, including properties for objects and items for arrays.";
        }

        if (rule.MinLength < 0 || rule.MaxLength < 0 || rule.MinLength > rule.MaxLength ||
            rule.MinItems < 0 || rule.MaxItems < 0 || rule.MinItems > rule.MaxItems ||
            rule.Minimum > rule.Maximum || rule.MaxDecimalPlaces is < 0 or > 28 ||
            rule.AllowedValues is { Length: 0 } || rule.AllowedValues?.Any(value => value is null || !StringMatches(rule, value)) == true)
        {
            return $"{path} has invalid bounds or allowed values.";
        }

        if (rule.Properties is { } properties)
        {
            foreach ((string name, FieldRule? child) in properties)
            {
                if (child is null)
                {
                    return $"{path}.{name} must declare a field rule.";
                }

                if (ValidateRule(child, $"{path}.{name}") is { } error)
                {
                    return error;
                }
            }
        }

        if (rule.Items is { } items)
        {
            return items.Required ? $"{path}.items cannot specify required." : ValidateRule(items, $"{path}.items");
        }

        return null;
    }

    private static string? ValidateValue(FieldRule rule, JsonElement value, string path) => rule.Type switch
    {
        "object" when value.ValueKind == JsonValueKind.Object => ValidateObject(rule.Properties!, value, path),
        "array" when value.ValueKind == JsonValueKind.Array => ValidateArray(rule, value, path),
        "string" when value.ValueKind == JsonValueKind.String => ValidateString(rule, value.GetString()!, path),
        "number" when value.ValueKind == JsonValueKind.Number => ValidateNumber(rule, value, path),
        "boolean" when value.ValueKind is JsonValueKind.True or JsonValueKind.False => null,
        "null" when value.ValueKind == JsonValueKind.Null => null,
        _ => $"{path} must be of type {rule.Type}."
    };

    private static string? ValidateObject(Dictionary<string, FieldRule?> properties, JsonElement value, string path)
    {
        HashSet<string> seen = new(StringComparer.Ordinal);
        foreach (JsonProperty property in value.EnumerateObject())
        {
            if (!properties.TryGetValue(property.Name, out FieldRule? rule))
            {
                return $"{path} contains a field that is not allowed.";
            }

            if (!seen.Add(property.Name))
            {
                return $"{path}.{property.Name} appears more than once.";
            }

            if (ValidateValue(rule!, property.Value, $"{path}.{property.Name}") is { } error)
            {
                return error;
            }
        }

        foreach ((string name, FieldRule? rule) in properties)
        {
            if (rule!.Required && !seen.Contains(name))
            {
                return $"{path}.{name} is required.";
            }
        }

        return null;
    }

    private static string? ValidateArray(FieldRule rule, JsonElement value, string path)
    {
        int count = value.GetArrayLength();
        if (count < rule.MinItems || count > rule.MaxItems)
        {
            return $"{path} has an invalid number of items.";
        }

        int index = 0;
        foreach (JsonElement item in value.EnumerateArray())
        {
            if (ValidateValue(rule.Items!, item, $"{path}[{index++}]") is { } error)
            {
                return error;
            }
        }

        return null;
    }

    private static bool StringMatches(FieldRule rule, string text) =>
        (!rule.NonBlank || !string.IsNullOrWhiteSpace(text)) && !(text.Length < rule.MinLength || text.Length > rule.MaxLength);

    private static string? ValidateString(FieldRule rule, string text, string path)
    {
        if (!StringMatches(rule, text) || rule.AllowedValues?.Contains(text, StringComparer.Ordinal) == false)
        {
            return $"{path} does not satisfy the configured string constraints.";
        }

        return null;
    }

    private static string? ValidateNumber(FieldRule rule, JsonElement value, string path)
    {
        if (!value.TryGetDecimal(out decimal number) || number < rule.Minimum || number > rule.Maximum ||
            (rule.MaxDecimalPlaces is { } places && decimal.Round(number, places) != number))
        {
            return $"{path} does not satisfy the configured decimal constraints.";
        }

        return null;
    }

    private sealed record ObjectContract(Dictionary<string, FieldRule?>? Properties);

    private sealed record FieldRule
    {
        public string? Type { get; init; }
        public bool Required { get; init; }
        public Dictionary<string, FieldRule?>? Properties { get; init; }
        public FieldRule? Items { get; init; }
        public int? MinLength { get; init; }
        public int? MaxLength { get; init; }
        public bool NonBlank { get; init; }
        public string[]? AllowedValues { get; init; }
        public decimal? Minimum { get; init; }
        public decimal? Maximum { get; init; }
        public int? MaxDecimalPlaces { get; init; }
        public int? MinItems { get; init; }
        public int? MaxItems { get; init; }
    }
}
