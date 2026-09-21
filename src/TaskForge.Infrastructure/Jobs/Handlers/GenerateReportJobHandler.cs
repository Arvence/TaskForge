using System.Text.Json;
using System.Text.Json.Serialization;

using TaskForge.Application.Abstractions.Execution;

namespace TaskForge.Infrastructure.Jobs.Handlers;

public sealed class GenerateReportJobHandler : IJobHandler
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        NumberHandling = JsonNumberHandling.Strict
    };

    public string JobType => "generate-report";

    public string? ValidatePayload(string payloadJson)
    {
        try
        {
            ReadPayload(payloadJson, CancellationToken.None);
            return null;
        }
        catch (NonRetryableJobException exception)
        {
            return exception.Message;
        }
    }

    public Task<string?> HandleAsync(string payloadJson, CancellationToken cancellationToken = default)
    {
        ReportPayload payload = ReadPayload(payloadJson, cancellationToken);
        SortedDictionary<string, CategoryTotal> categories = new(StringComparer.Ordinal);
        decimal totalAmount = 0;

        foreach (ReportEntry entry in payload.Entries!)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string category = entry.Category!.Trim();
            decimal amount = entry.Amount!.Value;
            categories.TryGetValue(category, out CategoryTotal? previous);
            categories[category] = new CategoryTotal(category, (previous?.EntryCount ?? 0) + 1, (previous?.TotalAmount ?? 0) + amount);
            totalAmount += amount;
        }

        ReportResult result = new(payload.Title!.Trim(), payload.Entries.Length, totalAmount, categories.Values.ToArray());
        cancellationToken.ThrowIfCancellationRequested();
        string resultJson = JsonSerializer.Serialize(result, SerializerOptions);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<string?>(resultJson);
    }

    private static ReportPayload ReadPayload(string payloadJson, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ReportPayload payload;
        try
        {
            payload = JsonSerializer.Deserialize<ReportPayload>(payloadJson, SerializerOptions)
                ?? throw new NonRetryableJobException("A report payload is required.");
        }
        catch (JsonException exception)
        {
            throw new NonRetryableJobException("The report payload is invalid.", exception);
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(payload.Title) || payload.Title.Length > 120)
        {
            throw new NonRetryableJobException("Report title must contain between 1 and 120 characters and cannot be blank.");
        }

        if (payload.Entries is not { Length: >= 1 and <= 1000 })
        {
            throw new NonRetryableJobException("A report must contain between 1 and 1000 entries.");
        }

        foreach (ReportEntry? entry in payload.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (entry is null || string.IsNullOrWhiteSpace(entry.Category) || entry.Category.Length > 80)
            {
                throw new NonRetryableJobException("Each report entry requires a nonblank category of at most 80 characters.");
            }

            if (entry.Amount is not decimal amount || amount < 0 || amount > 1_000_000_000_000m || decimal.Round(amount, 2) != amount)
            {
                throw new NonRetryableJobException("Each report entry requires an amount between 0 and 1000000000000 with at most two decimal places.");
            }
        }

        return payload;
    }

    private sealed record ReportPayload(string? Title, ReportEntry[]? Entries);

    private sealed record ReportEntry(string? Category, decimal? Amount);

    private sealed record CategoryTotal(string Category, int EntryCount, decimal TotalAmount);

    private sealed record ReportResult(string Title, int EntryCount, decimal TotalAmount, CategoryTotal[] Categories);
}
