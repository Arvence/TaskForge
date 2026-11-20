using System.Text;
using System.Text.Json;

using Microsoft.Extensions.Configuration;

using TaskForge.Api.Jobs;

namespace TaskForge.IntegrationTests.Api;

public sealed class JobPayloadPolicyTests
{
    private const string Contract = """
        {"properties":{
            "referenceId":{"type":"string","nonBlank":true,"maxLength":8},
            "entries":{"type":"array","required":true,"minItems":1,"maxItems":2,"items":{
                "type":"object","properties":{
                    "amount":{"type":"number","required":true,"minimum":0,"maximum":10,"maxDecimalPlaces":2},
                    "state":{"type":"string","allowedValues":["open","closed"]},
                    "metadata":{"type":"null"}
                }
            }}
        }}
        """;

    [Theory]
    [InlineData("""{"entries":[{"amount":0}]}""")]
    [InlineData("""{"referenceId":"opaque-1","entries":[{"amount":10,"state":"open","metadata":null},{"amount":0.01}]}""")]
    public void Valid_values_and_omitted_optional_fields_are_accepted(string payload)
    {
        Assert.Null(Validate(CreatePolicy(Contract), payload));
    }

    [Theory]
    [InlineData("""{}""", "entries")]
    [InlineData("""{"entries":[]}""", "entries")]
    [InlineData("""{"entries":[{"amount":1},{"amount":1},{"amount":1}]}""", "entries")]
    [InlineData("""{"entries":[{"amount":1,"amount":2}]}""", "amount")]
    [InlineData("""{"entries":[{"amount":1,"am\u006funt":2}]}""", "amount")]
    [InlineData("""{"entries":[{"Amount":1}]}""", "entries[0]")]
    [InlineData("""{"entries":[{"amount":-1}]}""", "amount")]
    [InlineData("""{"entries":[{"amount":10.01}]}""", "amount")]
    [InlineData("""{"entries":[{"amount":0.001}]}""", "amount")]
    [InlineData("""{"entries":[{"amount":"1"}]}""", "amount")]
    [InlineData("""{"entries":[{"amount":1,"state":"OPEN"}]}""", "state")]
    [InlineData("""{"entries":[{"amount":1,"metadata":{}}]}""", "metadata")]
    [InlineData("""{"referenceId":null,"entries":[{"amount":1}]}""", "referenceId")]
    [InlineData("""{"referenceId":" ","entries":[{"amount":1}]}""", "referenceId")]
    [InlineData("""{"referenceId":"too-large","entries":[{"amount":1}]}""", "referenceId")]
    public void Structural_and_value_errors_identify_the_field(string payload, string field)
    {
        Assert.Contains(field, Validate(CreatePolicy(Contract), payload));
    }

    [Theory]
    [InlineData("generate-report")]
    [InlineData("""{}""")]
    [InlineData("""{"properties":{},"extra":true}""")]
    [InlineData("""{"properties":{"value":{"type":"string","optional":true}}}""")]
    [InlineData("""{"properties":{"value":{"type":"string","type":"number"}}}""")]
    [InlineData("""{"properties":{"value":{"type":"string"},"value":{"type":"string"}}}""")]
    [InlineData("""{"properties":{"value":{"type":"any"}}}""")]
    [InlineData("""{"properties":{"value":{"type":"array"}}}""")]
    [InlineData("""{"properties":{"value":{"type":"number","minimum":10,"maximum":1}}}""")]
    [InlineData("""{"properties":{"value":{"type":"boolean","maxLength":1}}}""")]
    public void Invalid_contracts_fail_at_startup(string contract)
    {
        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() => CreatePolicy(contract));
        Assert.Contains("example-app:Jobs:example", error.Message);
    }

    [Fact]
    public void Former_job_settings_cannot_silently_survive_contract_migration()
    {
        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() => CreatePolicy(Contract, obsoleteSetting: "https://example.test"));
        Assert.Contains("only job setting", error.Message);
    }

    [Fact]
    public void ReferenceId_is_only_available_when_declared_and_uses_ordinary_field_rules()
    {
        JobPayloadPolicy empty = CreatePolicy("""{"properties":{}}""");
        Assert.Contains("not allowed", Validate(empty, """{"referenceId":"example"}"""));

        JobPayloadPolicy bounded = CreatePolicy("""{"properties":{"referenceId":{"type":"string","maxLength":180}}}""");
        Assert.Null(Validate(bounded, "{\"referenceId\":\"" + new string('x', 150) + "\"}"));
    }

    [Fact]
    public void Payload_limit_counts_utf8_bytes()
    {
        JobPayloadPolicy policy = CreatePolicy("""{"properties":{"text":{"type":"string"}}}""");
        string payload = "{\"text\":\"" + new string('é', 8186) + "\"}";
        Assert.Equal(16 * 1024 - 1, Encoding.UTF8.GetByteCount(payload));
        Assert.Null(Validate(policy, payload + " "));
        Assert.Contains("16384 UTF-8 bytes", Validate(policy, payload + "  "));
    }

    private static JobPayloadPolicy CreatePolicy(string contract, string? obsoleteSetting = null)
    {
        const string job = "JobAccess:Applications:example-app:Jobs:example";
        Dictionary<string, string?> settings = new() { [$"{job}:Contract"] = contract };
        if (obsoleteSetting is not null)
        {
            settings[$"{job}:Target"] = obsoleteSetting;
        }

        return new JobPayloadPolicy(new ConfigurationBuilder().AddInMemoryCollection(settings).Build());
    }

    private static string? Validate(JobPayloadPolicy policy, string payload)
    {
        using JsonDocument document = JsonDocument.Parse(payload);
        return policy.Validate("example-app", "EXAMPLE", document.RootElement, Encoding.UTF8.GetByteCount(payload));
    }
}
