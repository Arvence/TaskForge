using System.Text.Json.Serialization;

namespace TaskForge.SampleClient.Handlers;

public sealed record GenerateReportPayload(string? Title = null, ReportEntry?[]? Entries = null);

public sealed record ReportEntry(string? Category = null, [property: JsonNumberHandling(JsonNumberHandling.Strict)] decimal? Amount = null);
