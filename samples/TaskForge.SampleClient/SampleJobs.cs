using System.Text.Json;

using TaskForge.SampleClient.Handlers;
using TaskForge.SDK;
using TaskForge.SDK.Jobs;

namespace TaskForge.SampleClient;

public static class SampleJobs
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    public static GenerateReportPayload ExampleReport() => new("September expenses", [new("Travel", 125.50m), new("Supplies", 40.25m), new("Travel", 24.50m)]);

    public static Task<JobResponse> SubmitReportAsync(TaskForgeClient client, GenerateReportPayload payload, CancellationToken cancellationToken = default) =>
        client.SubmitAsync(new("generate-report", JsonSerializer.SerializeToElement(payload, SerializerOptions)), cancellationToken: cancellationToken);

    public static Task<JobResponse> SubmitHttpAsync(TaskForgeClient client, string url, CancellationToken cancellationToken = default) =>
        client.SubmitAsync(new("http-request", JsonSerializer.SerializeToElement(new HttpRequestPayload(url, "GET"), SerializerOptions)), cancellationToken: cancellationToken);
}
