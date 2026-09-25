using TaskForge.Application.Common.Exceptions;
using TaskForge.Application.Jobs;
using TaskForge.Application.Jobs.Models;

namespace TaskForge.Api.Jobs;

public static class ExecutionWaitEndpointRegistration
{
    public static IEndpointRouteBuilder MapTaskForgeExecutionWaitEndpoint(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/api/executions/wait", WaitAsync)
            .WithName("WaitForExecution")
            .WithTags("Executions")
            .WithSummary("Wait up to 30 seconds for one compatible execution assignment.")
            .Produces<ExecutionAssignmentResponse>()
            .Produces(StatusCodes.Status204NoContent)
            .ProducesValidationProblem();
        return endpoints;
    }

    private static async Task<IResult> WaitAsync(WaitForExecutionRequest request, JobDistributionService service, HttpContext context, CancellationToken cancellationToken)
    {
        context.Response.Headers.CacheControl = "no-store";
        try
        {
            JobExecutionAssignment? assignment = await service.WaitAsync(request.ApplicationId, request.WorkerId, request.SupportedTypes, request.WaitSeconds, cancellationToken);
            return assignment is null ? Results.NoContent() : Results.Ok(ExecutionAssignmentResponse.From(assignment));
        }
        catch (ApplicationValidationException exception)
        {
            return Results.ValidationProblem(exception.Errors.ToDictionary(error => error.Key, error => error.Value));
        }
    }
}
