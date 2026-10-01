using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace TaskForge.Debugging.Api;

internal sealed class TaskForgeDebugClient(HttpClient httpClient)
{
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);

    public Task<HealthResponse> GetHealthAsync(CancellationToken cancellationToken)
    {
        return GetAsync<HealthResponse>("api/health", cancellationToken);
    }

    public Task<JobStatisticsResponse> GetStatisticsAsync(CancellationToken cancellationToken)
    {
        return GetAsync<JobStatisticsResponse>("api/stats", cancellationToken);
    }

    public Task<JobPageResponse> GetJobsAsync(JobFilters filters, int pageSize, CancellationToken cancellationToken)
    {
        return GetAsync<JobPageResponse>(BuildJobsPath(filters, pageSize), cancellationToken);
    }

    internal static string BuildJobsPath(JobFilters filters, int pageSize)
    {
        List<string> query = [$"page=1", $"pageSize={pageSize}"];
        AddQueryValue(query, "status", filters.Status);
        AddQueryValue(query, "type", filters.Type);
        AddQueryValue(query, "priority", filters.Priority);
        AddQueryValue(query, "applicationId", filters.ApplicationId);
        return $"api/jobs?{string.Join('&', query)}";
    }

    private async Task<T> GetAsync<T>(string path, CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await httpClient.GetAsync(path, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            string details = await ReadErrorAsync(response, cancellationToken);
            throw new TaskForgeApiException(response.StatusCode, details);
        }

        T? result = await response.Content.ReadFromJsonAsync<T>(JsonOptions, cancellationToken);
        return result
            ?? throw new TaskForgeApiException(
                response.StatusCode,
                "The API returned an empty response.");
    }

    private static void AddQueryValue(ICollection<string> query, string name, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            query.Add($"{name}={Uri.EscapeDataString(value)}");
        }
    }

    private static async Task<string> ReadErrorAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        string body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(body))
        {
            return response.ReasonPhrase ?? "Unknown API error.";
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(body);
            JsonElement root = document.RootElement;
            if (root.TryGetProperty("message", out JsonElement message))
            {
                return message.GetString() ?? body;
            }

            if (root.TryGetProperty("title", out JsonElement title))
            {
                return title.GetString() ?? body;
            }
        }
        catch (JsonException)
        {
            // Plain-text API errors are useful as-is.
        }

        return body.Length <= 300 ? body : $"{body[..300]}...";
    }
}

internal sealed class TaskForgeApiException(HttpStatusCode statusCode, string message) : Exception(message)
{
    public HttpStatusCode StatusCode { get; } = statusCode;
}
