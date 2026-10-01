namespace TaskForge.SDK;

public sealed record TaskForgeClientOptions
{
    public Uri BaseUrl { get; init; } = new("http://localhost:8275");
    public required string ApplicationId { get; init; }
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(30);
}
