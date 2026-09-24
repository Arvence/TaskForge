using TaskForge.Application.Common.Exceptions;
using TaskForge.Application.Jobs;
using TaskForge.Application.Jobs.Models;

namespace TaskForge.UnitTests.Application.Jobs;

public sealed class JobFailurePolicyTests
{
    [Theory]
    [InlineData(" InvalidPayload ", true)]
    [InlineData("UnsupportedJobType", true)]
    [InlineData("NonRetryableJobException", true)]
    [InlineData("INVALIDPAYLOAD", true)]
    [InlineData("UnknownError", false)]
    [InlineData("Timeout", false)]
    [InlineData("CancellationRequested", false)]
    [InlineData("HostStopping", false)]
    [InlineData("Permanent", false)]
    public void Only_explicit_server_codes_are_permanent(string code, bool permanent)
    {
        ExecutionReport.Fail report = new(code, "  Failed.\nDetails.  ");

        Assert.Equal(code.Trim().ToLowerInvariant(), report.ErrorCode);
        Assert.Equal("Failed.\nDetails.", report.ErrorMessage);
        Assert.Equal(permanent, JobFailurePolicy.IsPermanent(report.ErrorCode));
    }

    [Theory]
    [InlineData(null, "failure", "ErrorCode")]
    [InlineData("  ", "failure", "ErrorCode")]
    [InlineData("code", null, "ErrorMessage")]
    [InlineData("code", "\r\n ", "ErrorMessage")]
    public void Failure_requires_nonblank_code_and_message(string? code, string? message, string field)
    {
        ApplicationValidationException error = Assert.Throws<ApplicationValidationException>(() => new ExecutionReport.Fail(code, message));
        Assert.Single(error.Errors[field]);
    }

    [Theory]
    [InlineData(99, 3999)]
    [InlineData(100, 4000)]
    public void Normalized_fields_fit_existing_storage_limits(int codeLength, int messageLength)
    {
        ExecutionReport.Fail report = new(" " + new string('C', codeLength) + " ", " " + new string('M', messageLength) + " ");
        Assert.Equal(new string('c', codeLength), report.ErrorCode);
        Assert.Equal(new string('M', messageLength), report.ErrorMessage);
    }

    [Theory]
    [InlineData(101, 4000, "ErrorCode")]
    [InlineData(100, 4001, "ErrorMessage")]
    public void Oversized_fields_are_rejected_without_truncation(int codeLength, int messageLength, string field)
    {
        ApplicationValidationException error = Assert.Throws<ApplicationValidationException>(() =>
            new ExecutionReport.Fail(new string('c', codeLength), new string('m', messageLength)));
        Assert.Single(error.Errors[field]);
    }
}
