using System.Text.Json.Serialization;
using TaskForge.Api.Jobs;
using TaskForge.Application.Common.Exceptions;
using TaskForge.Application.Jobs;
using TaskForge.Application.Jobs.Validation;
using TaskForge.Domain.Jobs;
using TaskForge.Infrastructure.Persistence;

var builder = WebApplication.CreateBuilder(args);

builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<SubmitJobValidator>();
builder.Services.AddScoped<JobManager>();
builder.Services.AddTaskForgeSqlite(
    builder.Configuration.GetConnectionString("TaskForge")
        ?? "Data Source=data/taskforge.db",
    builder.Environment.ContentRootPath);

var app = builder.Build();

await app.Services.InitializeTaskForgeDatabaseAsync();

app.MapGet("/", () => Results.Redirect("/api/health"));

app.MapGet("/api/health", () => Results.Ok(new
{
    Status = "Healthy",
    Service = "TaskForge.Api",
    TimestampUtc = DateTimeOffset.UtcNow
}));

app.MapPost("/api/jobs", async Task<IResult> (
    SubmitJobRequest request,
    JobManager jobManager,
    CancellationToken cancellationToken) =>
{
    try
    {
        Job job = await jobManager.SubmitAsync(
            request.ToCommand(),
            cancellationToken);

        return Results.Created($"/api/jobs/{job.Id}", JobResponse.From(job));
    }
    catch (ApplicationValidationException exception)
    {
        return Results.ValidationProblem(
            exception.Errors.ToDictionary(error => error.Key, error => error.Value));
    }
});

app.MapGet("/api/jobs", async (
    JobManager jobManager,
    CancellationToken cancellationToken) =>
{
    IReadOnlyList<Job> jobs = await jobManager.GetAllAsync(cancellationToken);
    return Results.Ok(jobs.Select(JobResponse.From));
});

app.MapGet("/api/jobs/{id:guid}", async Task<IResult> (
    Guid id,
    JobManager jobManager,
    CancellationToken cancellationToken) =>
{
    Job? job = await jobManager.GetByIdAsync(id, cancellationToken);
    return job is not null
        ? Results.Ok(JobResponse.From(job))
        : Results.NotFound(new { Message = $"Job '{id}' was not found." });
});

app.Run();
