using System.Text.Json.Serialization;
using TaskForge.Api.Jobs;
using TaskForge.Domain.Jobs;

var builder = WebApplication.CreateBuilder(args);

builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddSingleton<InMemoryJobStore>();

var app = builder.Build();

app.MapGet("/", () => Results.Redirect("/api/health"));

app.MapGet("/api/health", () => Results.Ok(new
{
    Status = "Healthy",
    Service = "TaskForge.Api",
    TimestampUtc = DateTimeOffset.UtcNow
}));

app.MapPost("/api/jobs", (SubmitJobRequest request, InMemoryJobStore store) =>
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
    store.Add(job);

    return Results.Created($"/api/jobs/{job.Id}", JobResponse.From(job));
});

app.MapGet("/api/jobs", (InMemoryJobStore store) =>
    Results.Ok(store.GetAll().Select(JobResponse.From)));

app.MapGet("/api/jobs/{id:guid}", (Guid id, InMemoryJobStore store) =>
    store.TryGet(id, out Job job)
        ? Results.Ok(JobResponse.From(job))
        : Results.NotFound(new { Message = $"Job '{id}' was not found." }));

app.Run();
