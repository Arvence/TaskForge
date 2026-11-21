using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

using TaskForge.SDK.Handlers;

namespace TaskForge.Cli;

public static class TaskForgeCli
{
    public static async Task<int> RunAsync(string[] args, Action<JobHandlerRegistryBuilder>? configureOperations = null, IReadOnlyCollection<string>? supportedTypes = null)
    {
        if (args is [] or ["help"] or ["--help"] or ["-h"])
        {
            Console.WriteLine("TaskForge CLI");
            Console.WriteLine("  run    Run one worker with its registered operations and job types.");
            Console.WriteLine("Configure TaskForge__BaseUrl, TaskForge__ApplicationId, TaskForge__ApiKey, and TaskForge__Worker__SlotCount.");
            return 0;
        }

        if (args is not ["run"])
        {
            Console.Error.WriteLine("Use 'run' to start the worker or 'help' for usage.");
            return 2;
        }

        try
        {
            HostApplicationBuilder builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { ContentRootPath = AppContext.BaseDirectory });
            builder.Configuration.AddJsonFile("clientsettings.json", optional: true).AddEnvironmentVariables();
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
        catch (Exception exception) when (exception is ArgumentException or FormatException or IOException or InvalidOperationException)
        {
            Console.Error.WriteLine(exception.Message);
            return 1;
        }
    }
}
