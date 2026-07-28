namespace TaskForge.Infrastructure.Jobs.Handlers;

public sealed class HttpRequestJobOptions
{
    public const string SectionName = "HttpRequestJobs";

    public string[] AllowedHosts { get; init; } = ["localhost", "127.0.0.1"];
}