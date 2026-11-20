namespace TaskForge.SampleClient.Handlers;

public sealed class HttpRequestJobOptions
{
    public const string SectionName = "HttpRequestJobs";

    public string? AllowedOrigin { get; init; }
}
