using System.Text;
using System.Text.Json;

using Microsoft.Extensions.Options;

using TaskForge.Application.Abstractions.Execution;

namespace TaskForge.Infrastructure.Jobs.Handlers;

public sealed class HttpRequestJobHandler(
    HttpClient httpClient,
    IOptions<HttpRequestJobOptions> options)
    : IJobHandler
{
    private static readonly JsonSerializerOptions SerializerOptions =
        new(JsonSerializerDefaults.Web);

    private static readonly HashSet<string> AllowedMethods =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "DELETE",
            "GET",
            "PATCH",
            "POST",
            "PUT"
        };

    private static readonly HashSet<string> RestrictedHeaders =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "Connection",
            "Content-Length",
            "Host",
            "Transfer-Encoding"
        };

    private readonly HashSet<string> _allowedHosts = (options.Value.AllowedHosts ?? [])
        .Where(host => !string.IsNullOrWhiteSpace(host))
        .Select(host => host.Trim())
        .ToHashSet(StringComparer.OrdinalIgnoreCase);

    public string JobType => "http-request";

    public async Task<string?> HandleAsync(
        string payloadJson,
        CancellationToken cancellationToken = default)
    {
        HttpRequestJobPayload payload = DeserializePayload(payloadJson);
        Uri uri = ValidateUri(payload.Url);
        string method = string.IsNullOrWhiteSpace(payload.Method)
            ? "POST"
            : payload.Method.Trim().ToUpperInvariant();

        if (!AllowedMethods.Contains(method))
        {
            throw new InvalidOperationException(
                $"HTTP method '{method}' is not supported.");
        }

        using HttpRequestMessage request = new(new HttpMethod(method), uri);
        if (payload.Body is { ValueKind: not JsonValueKind.Null })
        {
            request.Content = new StringContent(
                payload.Body.Value.GetRawText(),
                Encoding.UTF8,
                "application/json");
        }

        AddHeaders(request, payload.Headers);

        using HttpResponseMessage response = await httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"HTTP request returned {(int)response.StatusCode} "
                + $"({response.ReasonPhrase}).",
                null,
                response.StatusCode);
        }

        return JsonSerializer.Serialize(
            new HttpRequestJobResult(
                (int)response.StatusCode,
                response.ReasonPhrase),
            SerializerOptions);
    }

    private static HttpRequestJobPayload DeserializePayload(string payloadJson)
    {
        try
        {
            return JsonSerializer.Deserialize<HttpRequestJobPayload>(
                    payloadJson,
                    SerializerOptions)
                ?? throw new InvalidOperationException(
                    "An HTTP request payload is required.");
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException(
                "The HTTP request payload is invalid.",
                exception);
        }
    }

    private Uri ValidateUri(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri)
            || (uri.Scheme != Uri.UriSchemeHttp
                && uri.Scheme != Uri.UriSchemeHttps)
            || !string.IsNullOrEmpty(uri.UserInfo))
        {
            throw new InvalidOperationException(
                "URL must be an absolute HTTP or HTTPS URL without user information.");
        }

        if (!_allowedHosts.Contains(uri.DnsSafeHost))
        {
            throw new InvalidOperationException(
                $"HTTP host '{uri.DnsSafeHost}' is not allowed.");
        }

        return uri;
    }

    private static void AddHeaders(
        HttpRequestMessage request,
        IReadOnlyDictionary<string, string>? headers)
    {
        if (headers is null)
        {
            return;
        }

        foreach ((string name, string value) in headers)
        {
            if (string.IsNullOrWhiteSpace(name)
                || value is null
                || RestrictedHeaders.Contains(name)
                || value.Contains('\r')
                || value.Contains('\n'))
            {
                throw new InvalidOperationException(
                    $"HTTP header '{name}' is not allowed.");
            }

            if (request.Headers.TryAddWithoutValidation(name, value))
            {
                continue;
            }

            if (request.Content is null
                || !request.Content.Headers.TryAddWithoutValidation(name, value))
            {
                throw new InvalidOperationException(
                    $"HTTP header '{name}' is invalid for this request.");
            }
        }
    }

    private sealed record HttpRequestJobPayload(
        string Url,
        string? Method,
        JsonElement? Body,
        IReadOnlyDictionary<string, string>? Headers);

    private sealed record HttpRequestJobResult(
        int StatusCode,
        string? ReasonPhrase);
}