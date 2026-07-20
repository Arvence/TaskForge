var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

app.MapGet("/", () => Results.Redirect("/api/health"));

app.MapGet("/api/health", () => Results.Ok(new
{
    Status = "Healthy",
    Service = "TaskForge.Api",
    TimestampUtc = DateTimeOffset.UtcNow
}));

app.Run();
