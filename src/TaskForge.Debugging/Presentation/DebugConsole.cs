using TaskForge.Debugging.Api;
using TaskForge.Debugging.Configuration;

namespace TaskForge.Debugging.Presentation;

internal sealed class DebugConsole(bool useColor)
{
    public void WriteDashboard(
        DebugSettings settings,
        HealthResponse health,
        WorkerManagerSnapshot workers,
        JobPageResponse jobs)
    {
        WriteTitle("TASKFORGE DEBUG DASHBOARD");
        Console.WriteLine($"{settings.Environment} | {settings.GetApiBaseUri()}");
        Console.WriteLine();

        WriteSection("API HEALTH");
        WriteField("Service", health.Service);
        WriteField("Status", health.Status, GetStatusColor(health.Status));
        WriteField("API time", FormatTimestamp(health.TimestampUtc));

        Console.WriteLine();
        WriteSection("WORKERS");
        WriteField(
            "Count",
            $"{workers.ActiveWorkerCount} active / "
            + $"{workers.DesiredWorkerCount} desired");
        WriteWorkers(workers.Workers);

        Console.WriteLine();
        WriteSection("RECENT JOBS");
        WriteJobs(jobs);
    }

    public void WriteJobList(JobPageResponse jobs, JobFilters filters)
    {
        WriteTitle("TASKFORGE JOBS");
        List<string> activeFilters = [];
        AddFilter(activeFilters, "status", filters.Status);
        AddFilter(activeFilters, "type", filters.Type);
        AddFilter(activeFilters, "priority", filters.Priority);
        Console.WriteLine(
            activeFilters.Count == 0
                ? "Filters: none"
                : $"Filters: {string.Join(", ", activeFilters)}");
        Console.WriteLine();
        WriteJobs(jobs);
    }

    public static void WriteHelp()
    {
        Console.WriteLine("TaskForge.Debugging");
        Console.WriteLine();
        Console.WriteLine("  dashboard");
        Console.WriteLine("      Show API health, workers, and the five newest jobs.");
        Console.WriteLine();
        Console.WriteLine("  jobs [--status STATUS] [--type TYPE] [--priority PRIORITY]");
        Console.WriteLine("      List jobs using any combination of filters.");
        Console.WriteLine();
        Console.WriteLine("Examples:");
        Console.WriteLine("  dotnet run --project src/TaskForge.Debugging");
        Console.WriteLine("  dotnet run --project src/TaskForge.Debugging -- jobs --status Retrying");
        Console.WriteLine("  dotnet run --project src/TaskForge.Debugging -- jobs --type http-request --priority High");
    }

    private void WriteWorkers(IReadOnlyList<WorkerSnapshot> workers)
    {
        if (workers.Count == 0)
        {
            Console.WriteLine("  No active workers. Job processing is paused.");
            return;
        }

        Console.WriteLine("  WORKER          STATUS      CURRENT JOB                           STARTED (UTC)");
        foreach (WorkerSnapshot worker in workers)
        {
            Console.Write($"  {Trim(worker.Id, 14),-14}  ");
            WriteStatus(worker.Status, 10);
            Console.WriteLine(
                $"  {worker.CurrentJobId?.ToString() ?? "-",-36}  "
                + $"{worker.StartedAtUtc:yyyy-MM-dd HH:mm:ss}");
        }
    }

    private void WriteJobs(JobPageResponse jobs)
    {
        if (jobs.Items.Count == 0)
        {
            Console.WriteLine("  No jobs matched.");
            return;
        }

        Console.WriteLine("  JOB ID                                STATUS        PRIORITY  TYPE               RETRIES  UPDATED (UTC)");
        foreach (JobSummary job in jobs.Items)
        {
            Console.Write($"  {job.Id}  ");
            WriteStatus(job.Status, 12);
            Console.WriteLine(
                $"  {job.Priority,-8}  {Trim(job.Type, 17),-17}  "
                + $"{job.RetryCount}/{job.MaxRetries,-5}  "
                + $"{job.UpdatedAtUtc:yyyy-MM-dd HH:mm:ss}");
        }

        Console.WriteLine();
        Console.WriteLine($"  Showing {jobs.Items.Count} of {jobs.TotalCount} jobs.");
    }

    private void WriteTitle(string title)
    {
        WriteColored(title, ConsoleColor.DarkYellow);
        Console.WriteLine();
    }

    private void WriteSection(string title)
    {
        WriteColored(title, ConsoleColor.Cyan);
        Console.WriteLine();
    }

    private void WriteField(
        string name,
        string value,
        ConsoleColor? color = null)
    {
        Console.Write($"  {name,-10} ");
        if (color is ConsoleColor valueColor)
        {
            WriteColored(value, valueColor);
            Console.WriteLine();
            return;
        }

        Console.WriteLine(value);
    }

    private void WriteStatus(string status, int width)
    {
        string displayValue = Trim(status, width);
        WriteColored(displayValue, GetStatusColor(status));
        Console.Write(new string(' ', width - displayValue.Length));
    }

    private void WriteColored(string value, ConsoleColor color)
    {
        if (useColor)
        {
            Console.ForegroundColor = color;
        }

        Console.Write(value);

        if (useColor)
        {
            Console.ResetColor();
        }
    }

    private static ConsoleColor GetStatusColor(string status)
    {
        return status switch
        {
            "Healthy" or "Completed" or "Idle" => ConsoleColor.Green,
            "Pending" or "Queued" or "Starting" => ConsoleColor.Cyan,
            "Processing" or "Busy" => ConsoleColor.Yellow,
            "Retrying" or "Stopping" => ConsoleColor.DarkYellow,
            "DeadLettered" => ConsoleColor.Red,
            "Cancelled" or "Offline" => ConsoleColor.DarkGray,
            _ => ConsoleColor.Gray
        };
    }

    private static string FormatTimestamp(DateTimeOffset value)
    {
        return value.ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss 'UTC'");
    }

    private static void AddFilter(
        ICollection<string> filters,
        string name,
        string? value)
    {
        if (value is not null)
        {
            filters.Add($"{name}={value}");
        }
    }

    private static string Trim(string value, int maximumLength)
    {
        return value.Length <= maximumLength
            ? value
            : $"{value[..(maximumLength - 3)]}...";
    }
}
