using TaskForge.Debugging.Api;
using TaskForge.Debugging.Configuration;

namespace TaskForge.Debugging.Presentation;

internal sealed class DebugConsole(bool useColor)
{
    public void WriteDashboard(DebugSettings settings, HealthResponse health, JobStatisticsResponse statistics, JobPageResponse jobs)
    {
        WriteTitle("TASKFORGE DEBUG DASHBOARD");
        Console.WriteLine($"{settings.Environment} | {settings.GetApiBaseUri()}");
        Console.WriteLine();

        WriteSection("API HEALTH");
        WriteField("Service", health.Service);
        WriteField("Status", health.Status, GetStatusColor(health.Status));
        WriteField("API time", FormatTimestamp(health.TimestampUtc));

        Console.WriteLine();
        WriteSection("JOB STATISTICS (ALL APPLICATIONS)");
        WriteField("Total jobs", statistics.TotalJobs.ToString());
        JobStatusCounts counts = statistics.CountsByStatus;
        WriteField("Pending", counts.Pending.ToString());
        WriteField("Queued", counts.Queued.ToString());
        WriteField("Processing", counts.Processing.ToString());
        WriteField("Retrying", counts.Retrying.ToString());
        WriteField("Completed", counts.Completed.ToString());
        WriteField("DeadLettered", counts.DeadLettered.ToString());
        WriteField("Cancelled", counts.Cancelled.ToString());
        WriteField("Success rate", statistics.SuccessRatePercent is decimal rate ? $"{rate:0.##}%" : "N/A");

        Console.WriteLine();
        WriteSection("RECENT JOBS");
        WriteJobs(jobs);
    }

    public void WriteJobList(JobPageResponse jobs, JobFilters filters)
    {
        WriteTitle("TASKFORGE JOBS");
        List<string> activeFilters = [];
        AddFilter(activeFilters, "applicationId", filters.ApplicationId);
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
        Console.WriteLine("      Show API health, global job statistics, and the five newest jobs.");
        Console.WriteLine();
        Console.WriteLine("  jobs [--application-id ID] [--status STATUS] [--type TYPE] [--priority PRIORITY]");
        Console.WriteLine("      List up to 20 jobs using any combination of filters.");
        Console.WriteLine();
        Console.WriteLine("This console only reads server state; business execution belongs to external clients.");
        Console.WriteLine();
        Console.WriteLine("Examples:");
        Console.WriteLine("  dotnet run --project src/TaskForge.Debugging");
        Console.WriteLine("  dotnet run --project src/TaskForge.Debugging -- jobs --application-id example-app --status Retrying");
        Console.WriteLine("  dotnet run --project src/TaskForge.Debugging -- jobs --type http-request --priority High");
    }

    private void WriteJobs(JobPageResponse jobs)
    {
        if (jobs.Items.Count == 0)
        {
            Console.WriteLine("  No jobs matched.");
            return;
        }

        Console.WriteLine("  JOB ID                                STATUS        PRIORITY  APPLICATION        TYPE               RETRIES  UPDATED (UTC)");
        foreach (JobSummary job in jobs.Items)
        {
            Console.Write($"  {job.Id}  ");
            WriteStatus(job.Status, 12);
            Console.WriteLine(
                $"  {job.Priority,-8}  {Trim(job.ApplicationId, 17),-17}  {Trim(job.Type, 17),-17}  "
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

    private void WriteField(string name, string value, ConsoleColor? color = null)
    {
        Console.Write($"  {name,-14} ");
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
            "Healthy" or "Completed" => ConsoleColor.Green,
            "Pending" or "Queued" => ConsoleColor.Cyan,
            "Processing" => ConsoleColor.Yellow,
            "Retrying" => ConsoleColor.DarkYellow,
            "DeadLettered" => ConsoleColor.Red,
            "Cancelled" => ConsoleColor.DarkGray,
            _ => ConsoleColor.Gray
        };
    }

    private static string FormatTimestamp(DateTimeOffset value)
    {
        return value.ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss 'UTC'");
    }

    private static void AddFilter(ICollection<string> filters, string name, string? value)
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
