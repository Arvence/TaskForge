using System.Text.Json;
using System.Text.Json.Serialization;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

using TaskForge.SampleClient.Handlers;
using TaskForge.SDK;
using TaskForge.SDK.Execution;
using TaskForge.SDK.Jobs;

namespace TaskForge.SampleClient;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        if (args.Length == 0 || args is ["help"] or ["--help"] or ["-h"])
        {
            PrintUsage();
            return 0;
        }

        if (args is not (["run"] or ["submit-report"] or ["submit-report", _] or ["submit-http", _]))
        {
            PrintUsage();
            return 2;
        }

        try
        {
            HostApplicationBuilder builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { ContentRootPath = AppContext.BaseDirectory });
            builder.Configuration.AddJsonFile("clientsettings.json", optional: false).AddEnvironmentVariables();
            using HttpClient http = new() { Timeout = Timeout.InfiniteTimeSpan };
            TaskForgeClient client = new(http, new()
            {
                BaseUrl = new Uri(builder.Configuration["TaskForge:BaseUrl"] ?? "http://localhost:8275"),
                ApplicationId = builder.Configuration["TaskForge:ApplicationId"] ?? "a-project"
            });
            builder.Services.AddSingleton(client);
            builder.Services.AddSampleHandlers(builder.Configuration);
            if (args[0] == "run")
            {
                builder.Services.AddTaskForgeWorker();
                using IHost host = builder.Build();
                Console.WriteLine($"Executing generate-report and http-request jobs for {client.ApplicationId}. Press Ctrl+C to stop.");
                await host.RunAsync();
                return 0;
            }

            JobResponse job;
            if (args[0] == "submit-report")
            {
                GenerateReportPayload payload = SampleJobs.ExampleReport();
                if (args.Length == 2)
                {
                    string json = await File.ReadAllTextAsync(args[1]);
                    payload = JsonSerializer.Deserialize<GenerateReportPayload>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web))
                        ?? throw new JsonException("A report payload is required.");
                }

                job = await SampleJobs.SubmitReportAsync(client, payload);
            }
            else
            {
                job = await SampleJobs.SubmitHttpAsync(client, args[1]);
            }

            JsonSerializerOptions output = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };
            Console.WriteLine(JsonSerializer.Serialize(new { job.Id, job.ApplicationId, job.Type, job.Status }, output));
            return 0;
        }
        catch (Exception exception) when (exception is HttpRequestException or TimeoutException or JsonException or IOException or ArgumentException or InvalidOperationException)
        {
            Console.Error.WriteLine(exception.Message);
            return 1;
        }
    }

    private static void PrintUsage()
    {
        Console.WriteLine("TaskForge sample client");
        Console.WriteLine("  run                         Run the external worker until Ctrl+C.");
        Console.WriteLine("  submit-report [payload.json] Submit an expense report (built-in example by default).");
        Console.WriteLine("  submit-http <url>           Submit an HTTP GET for the client's allowed hosts.");
        Console.WriteLine("Configure clientsettings.json or TaskForge__BaseUrl / TaskForge__ApplicationId environment variables.");
    }
}
