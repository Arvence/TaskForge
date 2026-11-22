using System.Text.Json;
using System.Text.Json.Serialization;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

using TaskForge.SDK;
using TaskForge.SDK.Handlers;
using TaskForge.SDK.Jobs;

namespace TaskForge.Cli;

public static class TaskForgeCli
{
    private static readonly JsonSerializerOptions OutputOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    public static async Task<int> RunAsync(string[] args, Action<JobHandlerRegistryBuilder>? configureOperations = null, IReadOnlyCollection<string>? supportedTypes = null)
    {
        if (args is [] or ["help"] or ["--help"] or ["-h"])
        {
            Console.WriteLine("TaskForge CLI");
            Console.WriteLine("  run    Run one worker with its registered operations and job types.");
            Console.WriteLine("  submit <payload.json> [options]    Submit JSON. The default job type is general.");
            Console.WriteLine("    --type <name>                 --priority <Low|Normal|High|Critical>");
            Console.WriteLine("    --max-retries <count>         --timeout-seconds <seconds>");
            Console.WriteLine("    --run-at <ISO_8601_WITH_OFFSET> --idempotency-key <key>");
            Console.WriteLine("Configure TaskForge__BaseUrl, TaskForge__ApplicationId, TaskForge__ApiKey, and TaskForge__Worker__SlotCount.");
            return 0;
        }

        SubmitCommand? submission = null;
        if (args[0] == "submit")
        {
            try
            {
                submission = SubmitCommand.Parse(args);
            }
            catch (ArgumentException exception)
            {
                Console.Error.WriteLine(exception.Message);
                return 2;
            }
        }
        else if (args is not ["run"])
        {
            Console.Error.WriteLine("Use 'submit' to submit a job, 'run' to start the worker, or 'help' for usage.");
            return 2;
        }

        try
        {
            HostApplicationBuilder builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { ContentRootPath = AppContext.BaseDirectory });
            builder.Configuration.AddJsonFile("clientsettings.json", optional: true).AddEnvironmentVariables();
            if (submission is not null)
            {
                builder.Services.AddTaskForgeCliClient(builder.Configuration);
                using IHost submitHost = builder.Build();
                JobResponse job = await submission.ExecuteAsync(submitHost.Services.GetRequiredService<TaskForgeClient>());
                Console.WriteLine(JsonSerializer.Serialize(new { job.Id, job.ApplicationId, job.Type, job.Status, job.ScheduledAtUtc }, OutputOptions));
                return 0;
            }

            builder.Services.AddTaskForgeCliWorker(builder.Configuration, configureOperations, supportedTypes);
            using IHost host = builder.Build();
            if (host.Services.GetRequiredService<JobHandlerRegistry>().RegisteredTypes.Count == 0)
            {
                Console.Error.WriteLine("Register at least one operation and advertise a job type before running the worker.");
                return 1;
            }

            await host.RunAsync();
            return 0;
        }
        catch (TaskForgeApiException exception)
        {
            Console.Error.WriteLine($"TaskForge returned HTTP {(int?)exception.StatusCode}. {exception.Message}");
            foreach ((string field, string[] errors) in exception.Error.Errors ?? [])
            {
                if (errors is { Length: > 0 })
                {
                    Console.Error.WriteLine($"{field} - {string.Join(" ", errors)}");
                }
            }

            return 1;
        }
        catch (Exception exception) when (exception is ArgumentException or FormatException or HttpRequestException or TimeoutException or JsonException or IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine(exception.Message);
            return 1;
        }
    }
}
