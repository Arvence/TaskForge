using System.Text.Json.Serialization;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;

using TaskForge.Api.Jobs;
using TaskForge.Application.Abstractions.Persistence;
using TaskForge.Application.Common.Exceptions;
using TaskForge.Application.Jobs;
using TaskForge.Application.Jobs.Models;
using TaskForge.Application.Jobs.Validation;
using TaskForge.Application.Workers;
using TaskForge.Domain.Jobs;
using TaskForge.Infrastructure.Persistence;

var builder = WebApplication.CreateBuilder(args);

string? connectionString = builder.Configuration.GetConnectionString("TaskForge");
if (string.IsNullOrWhiteSpace(connectionString))
{
    throw new InvalidOperationException(
        "The TaskForge SQL Server connection string is required. Set ConnectionStrings:TaskForge or the ConnectionStrings__TaskForge environment variable.");
}

builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
// Swagger reads MVC JSON options when generating response schemas.
builder.Services.Configure<Microsoft.AspNetCore.Mvc.JsonOptions>(options =>
    options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options => options.SwaggerDoc("v1", new()
{
    Title = "TaskForge.Api",
    Version = typeof(Program).Assembly.GetName().Version!.ToString(3)
}));
builder.Services.AddProblemDetails();
builder.Services.Configure<RouteHandlerOptions>(options => options.ThrowOnBadRequest = true);
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddScoped<SubmitJobValidator>();
builder.Services.AddScoped<JobManager>();
builder.Services.AddScoped<JobCancellationService>();
builder.Services.AddSingleton<JobDistributionService>();
builder.Services.AddTaskForgeExecutionReporting();
builder.Services
    .AddOptions<WorkerOptions>()
    .Bind(builder.Configuration.GetSection(WorkerOptions.SectionName))
    .Validate(
        options => options.PollIntervalMilliseconds is >= 50 and <= 60_000,
        "Worker poll interval must be between 50 and 60000 milliseconds.")
    .Validate(
        options => options.RetryDelaySeconds is >= 1 and <= 3600,
        "Worker retry delay must be between 1 and 3600 seconds.")
    .Validate(
        options => options.MaxRetryDelaySeconds is >= 1 and <= 86_400
            && options.MaxRetryDelaySeconds >= options.RetryDelaySeconds,
        "Worker maximum retry delay must be between RetryDelaySeconds and 86400 seconds.")
    .Validate(
        options => options.LeaseGraceSeconds is >= 1 and <= 3600,
        "Worker lease grace period must be between 1 and 3600 seconds.")
    .ValidateOnStart();
builder.Services.AddSingleton<JobRetryPolicy>();
builder.Services.AddHostedService<JobMaintenanceService>();
builder.Services.AddTaskForgeSqlServer(connectionString);

var app = builder.Build();

await app.Services.InitializeTaskForgeDatabaseAsync();

app.UseExceptionHandler(errorApp => errorApp.Run(async context =>
{
    Exception? exception = context.Features.Get<IExceptionHandlerFeature>()?.Error;
    int statusCode = exception is BadHttpRequestException badRequest
        ? badRequest.StatusCode
        : StatusCodes.Status500InternalServerError;

    await Results.Problem(statusCode: statusCode).ExecuteAsync(context);
}));

app.UseStatusCodePages();

app.UseSwagger(options =>
    options.RouteTemplate = "openapi/{documentName}.json");

app.MapGet("/", () => Results.Redirect("/api/health"))
    .Produces(StatusCodes.Status302Found);
app.MapTaskForgeExecutionWaitEndpoint();
app.MapTaskForgeExecutionEndpoints();

app.MapGet("/api/health", () => Results.Ok(new HealthResponse("Healthy", "TaskForge.Api", DateTimeOffset.UtcNow)))
    .WithName("GetHealth")
    .ProducesProblem(StatusCodes.Status500InternalServerError)
    .WithTags("Health")
    .Produces<HealthResponse>();

app.MapGet("/api/ready", async (TaskForgeDbContext dbContext, CancellationToken cancellationToken) =>
{
    bool ready = await dbContext.Database.CanConnectAsync(cancellationToken);
    return Results.Json(
        new ReadinessResponse(ready ? "Ready" : "Unavailable"),
        statusCode: ready ? StatusCodes.Status200OK : StatusCodes.Status503ServiceUnavailable);
})
    .WithName("GetReadiness")
    .ProducesProblem(StatusCodes.Status500InternalServerError)
    .WithTags("Health")
    .WithSummary("Check connectivity to the configured SQL Server database.")
    .Produces<ReadinessResponse>(StatusCodes.Status200OK)
    .Produces<ReadinessResponse>(StatusCodes.Status503ServiceUnavailable);

app.MapGet("/api/stats", async (IJobStatisticsReader statisticsReader, CancellationToken cancellationToken) =>
    Results.Ok(await statisticsReader.GetAsync(cancellationToken)))
    .WithName("GetStats")
    .ProducesProblem(StatusCodes.Status500InternalServerError)
    .WithTags("Stats")
    .WithSummary("Get current status counts for all stored jobs.")
    .WithDescription(
        "Includes every job in the database without filtering or pagination. "
        + "Counts reflect current states, not historical transitions or attempts. Missing statuses have zero counts. "
        + "totalJobs is the sum of all seven status counts; there is no separate Failed status. "
        + "Consecutive responses may differ while workers update jobs. "
        + "The result does not guarantee a historical or transactionally consistent point-in-time snapshot. "
        + "retrying counts jobs waiting for a retry, not retry attempts or the sum of RetryCount values. "
        + "A processing job with cancellation requested remains in processing until its persisted status becomes Cancelled. "
        + "Success rate is completed / (completed + deadLettered) * 100, rounded to two decimal places "
        + "with midpoint ties away from zero; it is null when the denominator is zero. "
        + "Pending, queued, processing, retrying, and cancelled jobs are excluded from the denominator. "
        + "An empty database returns 200 OK with zero counts and a null success rate.")
    .Produces<JobStatistics>(StatusCodes.Status200OK);

app.MapPost("/api/jobs", async Task<IResult> (SubmitJobRequest request, [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey, JobManager jobManager, CancellationToken cancellationToken) =>
{
    try
    {
        JobSubmissionResult submission = await jobManager.SubmitAsync(
            request.ToCommand(),
            idempotencyKey,
            cancellationToken);

        JobResponse response = JobResponse.From(submission.Job);
        return submission.Created
            ? Results.Created($"/api/jobs/{submission.Job.Id}", response)
            : Results.Ok(response);
    }
    catch (ApplicationValidationException exception)
    {
        return Results.ValidationProblem(
            exception.Errors.ToDictionary(error => error.Key, error => error.Value));
    }
    catch (IdempotencyConflictException exception)
    {
        return Results.Conflict(new IdempotencyConflictResponse(exception.Message, exception.IdempotencyKey));
    }
})
    .WithName("SubmitJob")
    .ProducesProblem(StatusCodes.Status500InternalServerError)
    .WithTags("Jobs")
    .WithSummary("Submit a durable background job.")
    .Produces<JobResponse>(StatusCodes.Status201Created)
    .Produces<JobResponse>(StatusCodes.Status200OK)
    .ProducesValidationProblem()
    .Produces<IdempotencyConflictResponse>(StatusCodes.Status409Conflict)
    .ProducesProblem(StatusCodes.Status415UnsupportedMediaType);

app.MapGet("/api/jobs", async Task<IResult> (JobManager jobManager, CancellationToken cancellationToken, [FromQuery] JobStatus? status = null, [FromQuery] string? type = null, [FromQuery] JobPriority? priority = null, [FromQuery] int page = 1, [FromQuery] int pageSize = ListJobsQuery.DefaultPageSize, [FromQuery] string? applicationId = null) =>
{
    try
    {
        JobPage result = await jobManager.GetPageAsync(
            new ListJobsQuery(status, type, priority, page, pageSize, applicationId),
            cancellationToken);
        return Results.Ok(JobPageResponse.From(result));
    }
    catch (ApplicationValidationException exception)
    {
        return Results.ValidationProblem(
            exception.Errors.ToDictionary(error => error.Key, error => error.Value));
    }
})
    .WithName("ListJobs")
    .ProducesProblem(StatusCodes.Status500InternalServerError)
    .WithTags("Jobs")
    .WithSummary("Filter and page jobs in newest-first order.")
    .Produces<JobPageResponse>()
    .ProducesValidationProblem();

app.MapGet("/api/jobs/{id:guid}", async Task<IResult> (Guid id, JobManager jobManager, CancellationToken cancellationToken) =>
{
    Job? job = await jobManager.GetByIdAsync(id, cancellationToken);
    return job is not null
        ? Results.Ok(JobResponse.From(job))
        : Results.NotFound(new ApiErrorResponse($"Job '{id}' was not found."));
})
    .WithName("GetJob")
    .ProducesProblem(StatusCodes.Status500InternalServerError)
    .WithTags("Jobs")
    .WithSummary("Get a job by ID.")
    .Produces<JobResponse>()
    .Produces<ApiErrorResponse>(StatusCodes.Status404NotFound);

app.MapGet("/api/jobs/{id:guid}/attempts", async Task<IResult> (Guid id, IJobAttemptReader attemptReader, CancellationToken cancellationToken) =>
{
    JobAttemptHistory? history = await attemptReader.GetAttemptsAsync(id, cancellationToken);
    return history is null
        ? Results.NotFound(new ApiErrorResponse($"Job '{id}' was not found."))
        : Results.Ok(history.Attempts.Select(attempt => JobAttemptResponse.From(attempt, history.TimeoutSeconds)).ToArray());
})
    .WithName("GetJobAttempts")
    .ProducesProblem(StatusCodes.Status500InternalServerError)
    .WithTags("Jobs")
    .WithSummary("Get execution attempts in ascending attempt-number order.")
    .WithDescription("Includes persisted attemptId and deadlineAtUtc derived from startedAtUtc plus the job timeout. Lease grace does not extend the deadline.")
    .Produces<JobAttemptResponse[]>()
    .Produces<ApiErrorResponse>(StatusCodes.Status404NotFound);

app.MapPost("/api/jobs/{id:guid}/retry", async Task<IResult> (Guid id, JobManager jobManager, CancellationToken cancellationToken) =>
{
    try
    {
        JobReplayResult result = await jobManager.ReplayAsync(id, cancellationToken);
        return result.Status switch
        {
            JobReplayStatus.Created =>
                Results.Created($"/api/jobs/{result.Job!.Id}", JobResponse.From(result.Job)),
            JobReplayStatus.NotFound =>
                Results.NotFound(new ApiErrorResponse($"Job '{id}' was not found.")),
            _ => Results.Conflict(new JobConflictResponse($"Job '{id}' cannot be replayed. Only DeadLettered or Cancelled jobs can be replayed.", result.Job!.Status))
        };
    }
    catch (ApplicationValidationException exception)
    {
        return Results.ValidationProblem(
            exception.Errors.ToDictionary(error => error.Key, error => error.Value));
    }
})
    .WithName("ReplayJob")
    .ProducesProblem(StatusCodes.Status500InternalServerError)
    .WithTags("Jobs")
    .WithSummary("Replay a dead-lettered or cancelled job as a new execution.")
    .WithDescription(
        "Preserves type, payload, priority, maximum retries, and timeout, using current submission validation. "
        + "The original job and its attempts remain unchanged. The new job has no idempotency key; "
        + "the Idempotency-Key header is ignored and each successful call creates a separate job.")
    .Produces<JobResponse>(StatusCodes.Status201Created)
    .ProducesValidationProblem()
    .Produces<ApiErrorResponse>(StatusCodes.Status404NotFound)
    .Produces<JobConflictResponse>(StatusCodes.Status409Conflict);

app.MapPost("/api/jobs/{id:guid}/cancel", async Task<IResult> (Guid id, JobCancellationService cancellationService, CancellationToken cancellationToken) =>
{
    JobCancellationResult result = await cancellationService.RequestAsync(
        id,
        cancellationToken);

    return result.Status switch
    {
        JobCancellationStatus.Accepted =>
            Results.Ok(JobResponse.From(result.Job!)),
        JobCancellationStatus.NotFound =>
            Results.NotFound(new ApiErrorResponse($"Job '{id}' was not found.")),
        JobCancellationStatus.AlreadyFinished =>
            Results.Conflict(new JobConflictResponse($"Job '{id}' is already finished.", result.Job!.Status)),
        _ => Results.Conflict(new JobConflictResponse($"Job '{id}' was updated concurrently; retry cancellation."))
    };
})
    .WithName("CancelJob")
    .ProducesProblem(StatusCodes.Status500InternalServerError)
    .WithTags("Jobs")
    .WithSummary("Request cancellation of a queued or running job.")
    .Produces<JobResponse>()
    .Produces<ApiErrorResponse>(StatusCodes.Status404NotFound)
    .Produces<JobConflictResponse>(StatusCodes.Status409Conflict);

app.MapGet("/api/workers", () => Results.Problem(
    statusCode: StatusCodes.Status410Gone,
    detail: "Server worker management has been retired. External clients acquire work through POST /api/executions/wait."))
    .WithName("GetWorkers")
    .ProducesProblem(StatusCodes.Status500InternalServerError)
    .WithTags("Workers")
    .WithSummary("Retired server worker management endpoint.")
    .ProducesProblem(StatusCodes.Status410Gone);

app.MapPut("/api/workers/count", () => Results.Problem(
    statusCode: StatusCodes.Status410Gone,
    detail: "Server worker scaling has been retired. Configure execution capacity in external clients."))
    .WithName("SetWorkerCount")
    .ProducesProblem(StatusCodes.Status500InternalServerError)
    .WithTags("Workers")
    .WithSummary("Retired server worker scaling endpoint.")
    .ProducesProblem(StatusCodes.Status410Gone);

app.Run();

public partial class Program
{
}
