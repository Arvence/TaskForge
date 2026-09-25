using TaskForge.Application.Jobs.Models;
using TaskForge.Application.Jobs.Validation;
using TaskForge.Domain.Jobs;

namespace TaskForge.UnitTests.Application.Jobs;

public sealed class SubmitJobValidatorTests
{
    [Theory]
    [InlineData("", "{}", JobPriority.Normal, 0, 1, null, "Type")]
    [InlineData(" ", "{}", JobPriority.Normal, 0, 1, null, "Type")]
    [InlineData("send-email", "null", JobPriority.Normal, 0, 1, null, "Payload")]
    [InlineData("send-email", "{", JobPriority.Normal, 0, 1, null, "Payload")]
    [InlineData("send-email", "{}", (JobPriority)999, 0, 1, null, "Priority")]
    [InlineData("send-email", "{}", JobPriority.Normal, -1, 1, null, "MaxRetries")]
    [InlineData("send-email", "{}", JobPriority.Normal, 11, 1, null, "MaxRetries")]
    [InlineData("send-email", "{}", JobPriority.Normal, 0, 0, null, "TimeoutSeconds")]
    [InlineData("send-email", "{}", JobPriority.Normal, 0, 3601, null, "TimeoutSeconds")]
    [InlineData("send-email", "{}", JobPriority.Normal, 0, 1, " ", "Idempotency-Key")]
    public void Envelope_and_json_errors_are_preserved(string type, string payload, JobPriority priority, int maxRetries, int timeoutSeconds, string? idempotencyKey, string expectedField)
    {
        SubmitJobCommand command = new("client-app", type, payload, priority, maxRetries, timeoutSeconds);

        IReadOnlyDictionary<string, string[]> errors = new SubmitJobValidator().Validate(command, idempotencyKey);

        Assert.Equal(expectedField, Assert.Single(errors).Key);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(10, 3600)]
    public void Boundary_values_are_accepted_without_handlers(int maxRetries, int timeoutSeconds)
    {
        SubmitJobCommand command = new(new string('a', 100), new string('t', 100), "[]", JobPriority.High, maxRetries, timeoutSeconds);
        Assert.Empty(new SubmitJobValidator().Validate(command, new string('k', 100)));
    }

    [Fact]
    public void Oversized_type_and_idempotency_key_are_rejected()
    {
        SubmitJobCommand command = new("client-app", new string('t', 101), "{}", JobPriority.Normal, 0, 1);
        Assert.Equal(["Type", "Idempotency-Key"], new SubmitJobValidator().Validate(command, new string('k', 101)).Keys);
    }
}
