using TaskForge.Application.Abstractions.Persistence;
using TaskForge.Application.Common.Exceptions;
using TaskForge.Application.Jobs;
using TaskForge.Application.Jobs.Models;

namespace TaskForge.UnitTests.Application.Jobs;

public sealed class JobExecutionServiceTests
{
    [Theory]
    [InlineData(null, null)]
    [InlineData(" null ", null)]
    [InlineData(" { \"ok\" : true, \"items\" : [1, 2] } ", "{\"ok\":true,\"items\":[1,2]}")]
    [InlineData("\"\\u0061\"", "\"a\"")]
    [InlineData("false", "false")]
    [InlineData("12.50", "12.50")]
    public void Complete_normalizes_optional_json(string? input, string? expected)
    {
        Assert.Equal(expected, new ExecutionReport.Complete(input).ResultJson);
    }

    [Theory]
    [InlineData(3999)]
    [InlineData(4000)]
    [InlineData(4001)]
    public void Complete_enforces_serialized_storage_boundary(int length)
    {
        string json = "\"" + new string('a', length - 2) + "\"";
        if (length <= 4000)
        {
            Assert.Equal(json, new ExecutionReport.Complete("  " + json + "  ").ResultJson);
        }
        else
        {
            ApplicationValidationException error = Assert.Throws<ApplicationValidationException>(() => new ExecutionReport.Complete(json));
            Assert.Contains("4000", Assert.Single(error.Errors["Result"]));
        }
    }

    [Fact]
    public void Complete_checks_length_after_string_escaping()
    {
        string json = "\"" + new string('\u00e9', 667) + "\"";
        Assert.True(json.Length < 4000);
        Assert.Throws<ApplicationValidationException>(() => new ExecutionReport.Complete(json));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("not-json")]
    [InlineData("{\"value\":}")]
    [InlineData("{} {}")]
    public async Task Invalid_json_is_rejected_before_persistence(string json)
    {
        RejectAccessStore store = new();
        ApplicationValidationException error = await Assert.ThrowsAsync<ApplicationValidationException>(() =>
            new JobExecutionService(store).CompleteAsync("test-app", Guid.NewGuid(), Guid.NewGuid(), "worker", json));
        Assert.Equal("Result must be valid JSON or null.", Assert.Single(error.Errors["Result"]));
    }

    [Theory]
    [InlineData(null, "worker", "ApplicationId")]
    [InlineData("bad app", "worker", "ApplicationId")]
    [InlineData("app", null, "WorkerId")]
    [InlineData("app", "  ", "WorkerId")]
    public async Task Invalid_identity_is_rejected_before_persistence(string? applicationId, string? workerId, string field)
    {
        ApplicationValidationException error = await Assert.ThrowsAsync<ApplicationValidationException>(() =>
            new JobExecutionService(new RejectAccessStore()).CompleteAsync(applicationId, Guid.NewGuid(), Guid.NewGuid(), workerId));
        Assert.Single(error.Errors[field]);
        ApplicationValidationException failureError = await Assert.ThrowsAsync<ApplicationValidationException>(() =>
            new JobExecutionService(new RejectAccessStore()).FailAsync(applicationId, Guid.NewGuid(), Guid.NewGuid(), workerId, "Failure", "Failed."));
        Assert.Single(failureError.Errors[field]);
    }

    [Fact]
    public async Task Empty_ids_and_oversized_identity_are_rejected_before_persistence()
    {
        ApplicationValidationException error = await Assert.ThrowsAsync<ApplicationValidationException>(() =>
            new JobExecutionService(new RejectAccessStore()).CompleteAsync(new string('a', 101), Guid.Empty, Guid.Empty, new string('w', 201)));
        Assert.Equal(4, error.Errors.Count);
    }

    [Fact]
    public async Task Oversized_result_is_rejected_before_persistence()
    {
        await Assert.ThrowsAsync<ApplicationValidationException>(() => new JobExecutionService(new RejectAccessStore())
            .CompleteAsync("app", Guid.NewGuid(), Guid.NewGuid(), "worker", "\"" + new string('a', 3999) + "\""));
    }

    [Theory]
    [InlineData(null, "message")]
    [InlineData("code", null)]
    [InlineData("", "message")]
    public async Task Invalid_failure_is_rejected_before_persistence(string? code, string? message)
    {
        await Assert.ThrowsAsync<ApplicationValidationException>(() => new JobExecutionService(new RejectAccessStore())
            .FailAsync("app", Guid.NewGuid(), Guid.NewGuid(), "worker", code, message));
    }

    [Fact]
    public async Task Oversized_failure_is_rejected_before_persistence()
    {
        ApplicationValidationException error = await Assert.ThrowsAsync<ApplicationValidationException>(() => new JobExecutionService(new RejectAccessStore())
            .FailAsync("app", Guid.NewGuid(), Guid.NewGuid(), "worker", new string('c', 101), new string('m', 4001)));
        Assert.Equal(2, error.Errors.Count);
    }

    private sealed class RejectAccessStore : IExecutionStore
    {
        public Task<JobCancellationResult> CancelAsync(Guid jobId, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Validation must precede persistence.");

        public Task<ExecutionLookup> FindExecutionAsync(ExecutionIdentity identity, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Validation must precede persistence.");

        public Task<ExecutionResult> TransitionAsync(ExecutionIdentity identity, ExecutionReport report, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Validation must precede persistence.");
    }
}
