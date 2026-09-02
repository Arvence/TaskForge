using System.Text.Json.Serialization;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;

using TaskForge.Api.Jobs;
using TaskForge.Api.Workers;
using TaskForge.Application.Abstractions.Execution;
using TaskForge.Application.Common.Exceptions;
using TaskForge.Application.Jobs;
using TaskForge.Application.Jobs.Models;
using TaskForge.Application.Jobs.Validation;
using TaskForge.Application.Workers;
using TaskForge.Domain.Jobs;
using TaskForge.Infrastructure.Jobs.Handlers;
using TaskForge.Infrastructure.Persistence;

var builder = WebApplication.CreateBuilder(args);

builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddScoped<SubmitJobValidator>();
builder.Services.AddScoped<JobManager>();
builder.Services.AddScoped<JobCancellationService>();
builder.Services.AddSingleton<JobCancellationRegistry>();
builder.Services
    .AddOptions<WorkerOptions>()
    .Bind(builder.Configuration.GetSection(WorkerOptions.SectionName))
    .Validate(
        options => options.Count is >= 0 and <= WorkerOptions.MaximumWorkerCount,
        $"Worker count must be between 0 and {WorkerOptions.MaximumWorkerCount}.")
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
builder.Services
    .AddOptions<HttpRequestJobOptions>()
    .Bind(builder.Configuration.GetSection(HttpRequestJobOptions.SectionName))
    .Validate(
        options => options.AllowedHosts is not null
            && options.AllowedHosts.All(
                host => !string.IsNullOrWhiteSpace(host)),
        "HTTP request job allowed hosts cannot contain blank values.")
    .ValidateOnStart();
builder.Services.AddHttpClient<HttpRequestJobHandler>()
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
    {
        AllowAutoRedirect = false
    });
builder.Services.AddScoped<IJobHandler>(
    serviceProvider =>
        serviceProvider.GetRequiredService<HttpRequestJobHandler>());
builder.Services.AddScoped<JobExecutor>();
builder.Services.AddSingleton<WorkerManager>();
builder.Services.AddHostedService(
    serviceProvider => serviceProvider.GetRequiredService<WorkerManager>());
builder.Services.AddTaskForgeSqlite(
    builder.Configuration.GetConnectionString("TaskForge")
        ?? "Data Source=data/taskforge.db",
    builder.Environment.ContentRootPath);

var app = builder.Build();

await app.Services.InitializeTaskForgeDatabaseAsync();

app.UseSwagger(options =>
    options.RouteTemplate = "openapi/{documentName}.json");

app.MapGet("/", () => Results.Redirect("/api/health"));

app.MapGet("/api/health", () => Results.Ok(new
{
    Status = "Healthy",
    Service = "TaskForge.Api",
    TimestampUtc = DateTimeOffset.UtcNow
}));

app.MapPost("/api/jobs", async Task<IResult> (
    SubmitJobRequest request,
    [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
    JobManager jobManager,
    CancellationToken cancellationToken) =>
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
        return Results.Conflict(new
        {
            Message = exception.Message,
            exception.IdempotencyKey
        });
    }
})
    .WithName("SubmitJob")
    .WithTags("Jobs")
    .WithSummary("Submit a durable background job.")
    .Produces<JobResponse>(StatusCodes.Status201Created)
    .Produces<JobResponse>(StatusCodes.Status200OK)
    .ProducesValidationProblem()
    .Produces(StatusCodes.Status409Conflict);

app.MapGet("/api/jobs", async Task<IResult> (
    JobManager jobManager,
    CancellationToken cancellationToken,
    [FromQuery] JobStatus? status = null,
    [FromQuery] string? type = null,
    [FromQuery] JobPriority? priority = null,
    [FromQuery] int page = 1,
    [FromQuery] int pageSize = ListJobsQuery.DefaultPageSize) =>
{
    try
    {
        JobPage result = await jobManager.GetPageAsync(
            new ListJobsQuery(status, type, priority, page, pageSize),
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
    .WithTags("Jobs")
    .WithSummary("Filter and page jobs in newest-first order.")
    .Produces<JobPageResponse>()
    .ProducesValidationProblem();

app.MapGet("/api/jobs/{id:guid}", async Task<IResult> (
    Guid id,
    JobManager jobManager,
    CancellationToken cancellationToken) =>
{
    Job? job = await jobManager.GetByIdAsync(id, cancellationToken);
    return job is not null
        ? Results.Ok(JobResponse.From(job))
        : Results.NotFound(new { Message = $"Job '{id}' was not found." });
})
    .WithName("GetJob")
    .WithTags("Jobs")
    .WithSummary("Get a job by ID.")
    .Produces<JobResponse>()
    .Produces(StatusCodes.Status404NotFound);

app.MapPost("/api/jobs/{id:guid}/cancel", async Task<IResult> (
    Guid id,
    JobCancellationService cancellationService,
    CancellationToken cancellationToken) =>
{
    JobCancellationResult result = await cancellationService.RequestAsync(
        id,
        cancellationToken);

    return result.Status switch
    {
        JobCancellationStatus.Accepted =>
            Results.Ok(JobResponse.From(result.Job!)),
        JobCancellationStatus.NotFound =>
            Results.NotFound(new { Message = $"Job '{id}' was not found." }),
        JobCancellationStatus.AlreadyFinished =>
            Results.Conflict(new
            {
                Message = $"Job '{id}' is already finished.",
                Status = result.Job!.Status
            }),
        _ => Results.Conflict(new
        {
            Message = $"Job '{id}' was updated concurrently; retry cancellation."
        })
    };
})
    .WithName("CancelJob")
    .WithTags("Jobs")
    .WithSummary("Request cancellation of a queued or running job.")
    .Produces<JobResponse>()
    .Produces(StatusCodes.Status404NotFound)
    .Produces(StatusCodes.Status409Conflict);

app.MapGet("/api/workers", (WorkerManager workerManager) =>
    Results.Ok(workerManager.GetSnapshot()))
    .WithName("GetWorkers")
    .WithTags("Workers")
    .WithSummary("Get active workers and the desired worker count.")
    .Produces<WorkerManagerSnapshot>();

app.MapPut("/api/workers/count", async Task<IResult> (
    SetWorkerCountRequest request,
    WorkerManager workerManager,
    CancellationToken cancellationToken) =>
{
    if (request.Count is < 0 or > WorkerOptions.MaximumWorkerCount)
    {
        return Results.ValidationProblem(new Dictionary<string, string[]>
        {
            ["Count"] =
            [
                $"Worker count must be between 0 and "
                + $"{WorkerOptions.MaximumWorkerCount}."
            ]
        });
    }

    WorkerManagerSnapshot snapshot = await workerManager.SetWorkerCountAsync(
        request.Count,
        cancellationToken);
    return Results.Ok(snapshot);
})
    .WithName("SetWorkerCount")
    .WithTags("Workers")
    .WithSummary("Scale in-process workers from zero to eight.")
    .Produces<WorkerManagerSnapshot>()
    .ProducesValidationProblem();

app.Run();
