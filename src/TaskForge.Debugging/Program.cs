using System.Text.Json;

using TaskForge.Debugging.Api;
using TaskForge.Debugging.Commands;
using TaskForge.Debugging.Configuration;
using TaskForge.Debugging.Presentation;

const string SettingsFileName = "debugsettings.json";

using CancellationTokenSource shutdown = new();
Console.CancelKeyPress += (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    shutdown.Cancel();
};

try
{
    string settingsPath = Path.Combine(AppContext.BaseDirectory, SettingsFileName);
    DebugSettings settings = await DebugSettings.LoadAsync(
        settingsPath,
        shutdown.Token);
    DebugCommand command = DebugCommand.Parse(args);

    if (command.Kind == DebugCommandKind.Help)
    {
        DebugConsole.WriteHelp();
        return 0;
    }

    using HttpClient httpClient = new()
    {
        BaseAddress = settings.GetApiBaseUri(),
        Timeout = TimeSpan.FromSeconds(10)
    };
    string version = typeof(DebugSettings).Assembly.GetName().Version!.ToString(3);
    httpClient.DefaultRequestHeaders.UserAgent.ParseAdd($"TaskForge.Debugging/{version}");

    TaskForgeDebugClient client = new(httpClient);
    DebugConsole console = new(!Console.IsOutputRedirected);

    if (command.Kind == DebugCommandKind.Jobs)
    {
        JobFilters filters = command.Filters
            ?? throw new InvalidOperationException("Job filters were not provided.");
        JobPageResponse jobs = await client.GetJobsAsync(
            filters,
            pageSize: 20,
            shutdown.Token);
        console.WriteJobList(jobs, filters);
        return 0;
    }

    Task<HealthResponse> healthTask = client.GetHealthAsync(shutdown.Token);
    Task<WorkerManagerSnapshot> workersTask = client.GetWorkersAsync(shutdown.Token);
    Task<JobPageResponse> jobsTask = client.GetJobsAsync(
        JobFilters.Empty,
        pageSize: 5,
        shutdown.Token);

    await Task.WhenAll(healthTask, workersTask, jobsTask);
    console.WriteDashboard(
        settings,
        await healthTask,
        await workersTask,
        await jobsTask);
    return 0;
}
catch (TaskCanceledException) when (!shutdown.IsCancellationRequested)
{
    Console.Error.WriteLine("The TaskForge API request timed out after 10 seconds.");
    return 1;
}
catch (OperationCanceledException)
{
    Console.Error.WriteLine("TaskForge debugging was cancelled.");
    return 130;
}
catch (HttpRequestException exception)
{
    Console.Error.WriteLine($"Could not connect to TaskForge: {exception.Message}");
    Console.Error.WriteLine("Start the API with: dotnet run --project src/TaskForge.Api");
    return 1;
}
catch (TaskForgeApiException exception)
{
    Console.Error.WriteLine(
        $"TaskForge returned HTTP {(int)exception.StatusCode}: {exception.Message}");
    return 1;
}
catch (Exception exception) when (
    exception is ArgumentException
        or InvalidOperationException
        or IOException
        or JsonException)
{
    Console.Error.WriteLine(exception.Message);
    return 2;
}
