using System.Net;
using System.Text.Json;

using Microsoft.Extensions.Options;

using TaskForge.SDK.Handlers;

namespace TaskForge.SampleClient.Handlers;

public sealed class HttpRequestJobHandler(HttpClient httpClient, IOptions<HttpRequestJobOptions> options) : IJobHandler<HttpRequestPayload>, IJobHandler<JsonElement>
{
    private const int MaxUrlLength = 2048;
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);
    private readonly string? _allowedOrigin = ReadOrigin(options.Value.AllowedOrigin);

    Task<JsonElement?> IJobHandler<JsonElement>.HandleAsync(JsonElement payload, CancellationToken cancellationToken) =>
        HandleAsync(SamplePayload.Bind<HttpRequestPayload>(payload, "http-request"), cancellationToken);

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
        if (payload.Method != "GET")
        {
            throw new NonRetryableJobException("HTTP jobs require an explicit GET method.");
        }

        if (payload.Body is { ValueKind: not JsonValueKind.Null } || payload.Headers is not null)
        {
            throw new NonRetryableJobException("GET jobs must omit the body and headers or set them to null.");
        }

        if (payload.Url is { Length: > MaxUrlLength })
        {
            throw new NonRetryableJobException($"The HTTP URL must contain at most {MaxUrlLength} characters.");
        }

        if (!Uri.TryCreate(payload.Url, UriKind.Absolute, out Uri? uri) || uri.Scheme is not ("http" or "https") || !string.IsNullOrEmpty(uri.UserInfo))
        {
            throw new NonRetryableJobException("URL must be an absolute HTTP or HTTPS URL without user information.");
        }

        if (uri.Query.Length != 0 || uri.Fragment.Length != 0 ||
            !string.Equals(uri.GetLeftPart(UriPartial.Authority), _allowedOrigin, StringComparison.OrdinalIgnoreCase))
        {
            throw new NonRetryableJobException("The HTTP origin is not allowed or the URL contains a query or fragment.");
        }

        return new(HttpMethod.Get, uri);
    }

    private static string? ReadOrigin(string? value)
    {
        if (value is null)
        {
            return null;
        }

        if (!Uri.TryCreate(value, UriKind.Absolute, out Uri? uri) || uri.Scheme is not ("http" or "https") ||
            uri.UserInfo.Length != 0 || uri.AbsolutePath != "/" || uri.Query.Length != 0 || uri.Fragment.Length != 0)
        {
            throw new InvalidOperationException("Configure HttpRequestJobs:AllowedOrigin as an HTTP or HTTPS origin with no credentials, path, query, or fragment.");
        }

        return uri.GetLeftPart(UriPartial.Authority);
    }
}
