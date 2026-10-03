using System.Text.Json;

using TaskForge.SDK.Handlers;

namespace TaskForge.SampleClient.Handlers;

public sealed class GenerateReportJobHandler : IJobHandler<GenerateReportPayload>
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    public Task<JsonElement?> HandleAsync(GenerateReportPayload payload, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(payload.Title) || payload.Title.Length > 120)
        {
            throw new NonRetryableJobException("Report title must contain between 1 and 120 characters and cannot be blank.");
        }

        if (payload.Entries is not { Length: >= 1 and <= 1000 })
        {
            throw new NonRetryableJobException("A report must contain between 1 and 1000 entries.");
        }

        SortedDictionary<string, CategoryTotal> categories = new(StringComparer.Ordinal);
        decimal totalAmount = 0;
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

            string category = entry.Category.Trim();
            categories.TryGetValue(category, out CategoryTotal? previous);
            categories[category] = new(category, (previous?.EntryCount ?? 0) + 1, (previous?.TotalAmount ?? 0) + amount);
            totalAmount += amount;
        }

        cancellationToken.ThrowIfCancellationRequested();
        ReportResult report = new(payload.Title.Trim(), payload.Entries.Length, totalAmount, categories.Values.ToArray());
        JsonElement result = JsonSerializer.SerializeToElement(report, SerializerOptions);
        if (result.GetRawText().Length > 4000)
        {
            throw new NonRetryableJobException("Report result exceeds 4000 characters. Reduce the number of categories or the text lengths.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<JsonElement?>(result);
    }

    private sealed record CategoryTotal(string Category, int EntryCount, decimal TotalAmount);
    private sealed record ReportResult(string Title, int EntryCount, decimal TotalAmount, CategoryTotal[] Categories);
}
