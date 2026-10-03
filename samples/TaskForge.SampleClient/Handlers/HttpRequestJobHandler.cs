using System.Net;
using System.Text;
using System.Text.Json;

using Microsoft.Extensions.Options;

using TaskForge.SDK.Handlers;

namespace TaskForge.SampleClient.Handlers;

public sealed class HttpRequestJobHandler(HttpClient httpClient, IOptions<HttpRequestJobOptions> options) : IJobHandler<HttpRequestPayload>
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);
    private static readonly HashSet<string> AllowedMethods = new(StringComparer.OrdinalIgnoreCase) { "DELETE", "GET", "PATCH", "POST", "PUT" };
    private static readonly HashSet<string> RestrictedHeaders = new(StringComparer.OrdinalIgnoreCase) { "Connection", "Content-Length", "Host", "Transfer-Encoding" };
    private readonly HashSet<string> _allowedHosts = (options.Value.AllowedHosts ?? [])
        .Where(host => !string.IsNullOrWhiteSpace(host)).Select(host => host.Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase);

    public async Task<JsonElement?> HandleAsync(HttpRequestPayload payload, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using HttpRequestMessage request = CreateRequest(payload);
        using HttpResponseMessage response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            HttpRequestException exception = new($"HTTP request returned {(int)response.StatusCode} ({response.ReasonPhrase}).", null, response.StatusCode);
            if (response.StatusCode is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests || (int)response.StatusCode is >= 500 and <= 599)
            {
                throw exception;
            }

            throw new NonRetryableJobException(exception.Message, exception);
        }

        JsonElement result = JsonSerializer.SerializeToElement(new { statusCode = (int)response.StatusCode, response.ReasonPhrase }, SerializerOptions);
        if (result.GetRawText().Length > 4000)
        {
            throw new NonRetryableJobException("HTTP response metadata exceeds the 4000-character result limit.");
        }

        return result;
    }

    private HttpRequestMessage CreateRequest(HttpRequestPayload payload)
    {
        if (!Uri.TryCreate(payload.Url, UriKind.Absolute, out Uri? uri) || uri.Scheme is not ("http" or "https") || !string.IsNullOrEmpty(uri.UserInfo))
        {
            throw new NonRetryableJobException("URL must be an absolute HTTP or HTTPS URL without user information.");
        }

        if (!_allowedHosts.Contains(uri.DnsSafeHost))
        {
            throw new NonRetryableJobException($"HTTP host '{uri.DnsSafeHost}' is not allowed.");
        }

        string method = string.IsNullOrWhiteSpace(payload.Method) ? "POST" : payload.Method.Trim().ToUpperInvariant();
        if (!AllowedMethods.Contains(method))
        {
            throw new NonRetryableJobException($"HTTP method '{method}' is not supported.");
        }

        HttpRequestMessage request = new(new HttpMethod(method), uri);
        try
        {
            if (payload.Body is { ValueKind: not JsonValueKind.Null })
            {
                request.Content = new StringContent(payload.Body.Value.GetRawText(), Encoding.UTF8, "application/json");
            }

            foreach ((string name, string? value) in payload.Headers ?? new Dictionary<string, string?>())
            {
                if (string.IsNullOrWhiteSpace(name) || value is null || RestrictedHeaders.Contains(name) || value.Contains('\r') || value.Contains('\n'))
                {
                    throw new NonRetryableJobException($"HTTP header '{name}' is not allowed.");
                }

                if (!request.Headers.TryAddWithoutValidation(name, value)
                    && (request.Content is null || !request.Content.Headers.TryAddWithoutValidation(name, value)))
                {
                    throw new NonRetryableJobException($"HTTP header '{name}' is invalid for this request.");
                }
            }

            return request;
        }
        catch
        {
            request.Dispose();
            throw;
        }
    }
}
