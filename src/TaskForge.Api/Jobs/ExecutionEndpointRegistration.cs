using TaskForge.Application.Common.Exceptions;
using TaskForge.Application.Jobs;
using TaskForge.Application.Jobs.Models;

namespace TaskForge.Api.Jobs;

public static class ExecutionEndpointRegistration
{
    public static IServiceCollection AddTaskForgeExecutionReporting(this IServiceCollection services)
    {
        services.AddScoped<JobExecutionService>();
        return services;
    }

    public static IEndpointRouteBuilder MapTaskForgeExecutionEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/api/jobs/{jobId:guid}/attempts/{attemptId:guid}/complete", CompleteAsync)
            .WithName("CompleteExecution")
            .WithTags("Executions")
            .WithSummary("Complete an execution or acknowledge an identical accepted report.")
            .Produces<JobResponse>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);
        endpoints.MapPost("/api/jobs/{jobId:guid}/attempts/{attemptId:guid}/fail", FailAsync)
            .WithName("FailExecution")
            .WithTags("Executions")
            .WithSummary("Report an execution failure using server-owned retry and permanent-error policy.")
            .Produces<JobResponse>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);
        return endpoints;
    }

    private static async Task<IResult> CompleteAsync(Guid jobId, Guid attemptId, CompleteExecutionRequest request, JobExecutionService service, CancellationToken cancellationToken)
    {
        try
        {
            ExecutionReportResult result = await service.CompleteAsync(
                request.ApplicationId, jobId, attemptId, request.WorkerId, request.Result?.GetRawText(), cancellationToken);
            return ToResponse(result);
        }
        catch (ApplicationValidationException exception)
        {
            return Results.ValidationProblem(exception.Errors.ToDictionary(error => error.Key, error => error.Value));
        }
    }

    private static async Task<IResult> FailAsync(Guid jobId, Guid attemptId, FailExecutionRequest request, JobExecutionService service, CancellationToken cancellationToken)
    {
        try
        {
            ExecutionReportResult result = await service.FailAsync(
                request.ApplicationId, jobId, attemptId, request.WorkerId, request.ErrorCode, request.ErrorMessage, cancellationToken);
            return ToResponse(result);
        }
        catch (ApplicationValidationException exception)
        {
            return Results.ValidationProblem(exception.Errors.ToDictionary(error => error.Key, error => error.Value));
        }
    }

    private static IResult ToResponse(ExecutionReportResult result) => result.Status switch
    {
        ExecutionResult.Accepted or ExecutionResult.Duplicate => Results.Ok(JobResponse.From(result.Job!)),
        ExecutionResult.Missing => Results.Problem(statusCode: StatusCodes.Status404NotFound, detail: "The application, job, and attempt combination was not found."),
        _ => Results.Problem(statusCode: StatusCodes.Status409Conflict, detail: $"The execution report was rejected: {result.Status}.")
    };
}
