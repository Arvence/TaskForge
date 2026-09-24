using TaskForge.Application.Abstractions.Persistence;
using TaskForge.Application.Common.Exceptions;
using TaskForge.Application.Jobs.Models;
using TaskForge.Domain.Jobs;

namespace TaskForge.Application.Jobs;

public sealed class JobExecutionService(IExecutionStore executionStore)
{
    public Task<ExecutionReportResult> CompleteAsync(string? applicationId, Guid jobId, Guid attemptId, string? workerId, string? resultJson = null, CancellationToken cancellationToken = default)
    {
        ExecutionIdentity identity = ValidateIdentity(applicationId, jobId, attemptId, workerId);
        return ReportAsync(identity, new ExecutionReport.Complete(resultJson), cancellationToken);
    }

    public Task<ExecutionReportResult> FailAsync(string? applicationId, Guid jobId, Guid attemptId, string? workerId, string? errorCode, string? errorMessage, CancellationToken cancellationToken = default)
    {
        ExecutionIdentity identity = ValidateIdentity(applicationId, jobId, attemptId, workerId);
        return ReportAsync(identity, new ExecutionReport.Fail(errorCode, errorMessage), cancellationToken);
    }

    private async Task<ExecutionReportResult> ReportAsync(ExecutionIdentity identity, ExecutionReport report, CancellationToken cancellationToken)
    {
        ExecutionResult result = await executionStore.TransitionAsync(identity, report, cancellationToken);
        Job? job = result is ExecutionResult.Accepted or ExecutionResult.Duplicate
            ? (await executionStore.FindExecutionAsync(identity, cancellationToken)).Assignment?.Job : null;
        return new(result, job);
    }

    private static ExecutionIdentity ValidateIdentity(string? applicationId, Guid jobId, Guid attemptId, string? workerId)
    {
        Dictionary<string, string[]> errors = [];
        if (!JobApplicationId.IsValid(applicationId))
        {
            errors["ApplicationId"] = ["Application ID must contain 1 to 100 ASCII letters, digits, dots, underscores, or hyphens."];
        }

        if (string.IsNullOrWhiteSpace(workerId) || workerId.Length > 200)
        {
            errors["WorkerId"] = ["Worker ID must contain 1 to 200 characters and cannot be blank."];
        }

        if (jobId == Guid.Empty)
        {
            errors["JobId"] = ["Job ID must not be empty."];
        }

        if (attemptId == Guid.Empty)
        {
            errors["AttemptId"] = ["Attempt ID must not be empty."];
        }

        if (errors.Count > 0)
        {
            throw new ApplicationValidationException(errors);
        }

        return new(applicationId!, jobId, attemptId, workerId!);
    }
}
