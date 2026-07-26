using System.Text.Json.Serialization;
using TaskForge.Api.Jobs;
using TaskForge.Domain.Jobs;
using TaskForge.Infrastructure.Persistence;

var builder = WebApplication.CreateBuilder(args);

builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
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
    SqliteJobStore store,
    CancellationToken cancellationToken) =>
{
    Dictionary<string, string[]> errors = request.Validate();
    if (errors.Count > 0)
    {
        return Results.ValidationProblem(errors);
    }

    DateTimeOffset now = DateTimeOffset.UtcNow;
    Job job = new(
        Guid.NewGuid(),
        request.Type.Trim(),
        request.Payload.GetRawText(),
        request.Priority,
        request.MaxRetries,
        request.TimeoutSeconds,
        now);

    job.Queue(now);
    await store.AddAsync(job, cancellationToken);

    return Results.Created($"/api/jobs/{job.Id}", JobResponse.From(job));
});

app.MapGet("/api/jobs", async (
    SqliteJobStore store,
    CancellationToken cancellationToken) =>
{
    IReadOnlyList<Job> jobs = await store.GetAllAsync(cancellationToken);
    return Results.Ok(jobs.Select(JobResponse.From));
});

app.MapGet("/api/jobs/{id:guid}", async Task<IResult> (
    Guid id,
    SqliteJobStore store,
    CancellationToken cancellationToken) =>
{
    Job? job = await store.FindAsync(id, cancellationToken);
    return job is not null
        ? Results.Ok(JobResponse.From(job))
        : Results.NotFound(new { Message = $"Job '{id}' was not found." });
});

app.Run();
