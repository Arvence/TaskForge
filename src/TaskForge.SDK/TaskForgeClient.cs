using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

using TaskForge.SDK.Jobs;

namespace TaskForge.SDK;

public sealed class TaskForgeClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(allowIntegerValues: false) }
    };

    private readonly HttpClient _httpClient;
    private readonly Uri _baseUrl;
    private readonly string _applicationId;
    private readonly TimeSpan _requestTimeout;

    public TaskForgeClient(HttpClient httpClient, TaskForgeClientOptions options)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(options);
        if (options.BaseUrl is not { IsAbsoluteUri: true } baseUrl || baseUrl.Scheme is not ("http" or "https")
            || baseUrl.Query.Length > 0 || baseUrl.Fragment.Length > 0)
        {
            throw new ArgumentException("BaseUrl must be an absolute HTTP or HTTPS URL without a query or fragment.", nameof(options));
        }

        string? applicationId = options.ApplicationId?.Trim();
        if (string.IsNullOrEmpty(applicationId) || applicationId.Length > 100
            || applicationId.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not ('.' or '_' or '-')))
        {
            throw new ArgumentException("ApplicationId must contain 1 to 100 ASCII letters, digits, dots, underscores, or hyphens.", nameof(options));
        }

        if (options.RequestTimeout <= TimeSpan.Zero || options.RequestTimeout.TotalMilliseconds > int.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "RequestTimeout must be positive and at most Int32.MaxValue milliseconds.");
        }

        _httpClient = httpClient;
        _baseUrl = new Uri(baseUrl.AbsoluteUri.TrimEnd('/') + "/");
        _applicationId = applicationId.ToLowerInvariant();
        _requestTimeout = options.RequestTimeout;
    }

    public Task<JobResponse> SubmitAsync(SubmitJobRequest request, string? idempotencyKey = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return SendRequiredAsync<JobResponse>(HttpMethod.Post, "api/jobs", new
        {
            applicationId = _applicationId,
            request.Type,
            request.Payload,
            request.Priority,
            request.MaxRetries,
            request.TimeoutSeconds
        }, idempotencyKey, cancellationToken);
    }

    public Task<ExecutionAssignmentResponse?> WaitAsync(WaitForExecutionRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentOutOfRangeException.ThrowIfNegative(request.WaitSeconds);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(request.WaitSeconds, 30);
        TimeSpan timeout = TimeSpan.FromSeconds(Math.Max(_requestTimeout.TotalSeconds, request.WaitSeconds + 10));
        return SendAsync<ExecutionAssignmentResponse>(HttpMethod.Post, "api/executions/wait", new
        {
            applicationId = _applicationId,
            request.WorkerId,
            request.SupportedTypes,
            request.WaitSeconds
        }, null, timeout, allowNoContent: true, cancellationToken);
    }

    public Task<JobResponse> CompleteAsync(Guid jobId, Guid attemptId, CompleteExecutionRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return SendRequiredAsync<JobResponse>(HttpMethod.Post, $"api/jobs/{jobId:D}/attempts/{attemptId:D}/complete", new
        {
            applicationId = _applicationId,
            request.WorkerId,
            request.Result
        }, null, cancellationToken);
    }

    public Task<JobResponse> FailAsync(Guid jobId, Guid attemptId, FailExecutionRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return SendRequiredAsync<JobResponse>(HttpMethod.Post, $"api/jobs/{jobId:D}/attempts/{attemptId:D}/fail", new
        {
            applicationId = _applicationId,
            request.WorkerId,
            request.ErrorCode,
            request.ErrorMessage
        }, null, cancellationToken);
    }

    public Task<JobResponse> GetJobAsync(Guid jobId, CancellationToken cancellationToken = default) =>
        SendRequiredAsync<JobResponse>(HttpMethod.Get, $"api/jobs/{jobId:D}", null, null, cancellationToken);

    public async Task<IReadOnlyList<JobAttemptResponse>> GetAttemptsAsync(Guid jobId, CancellationToken cancellationToken = default) =>
        await SendRequiredAsync<JobAttemptResponse[]>(HttpMethod.Get, $"api/jobs/{jobId:D}/attempts", null, null, cancellationToken).ConfigureAwait(false);

    public Task<JobResponse> CancelAsync(Guid jobId, CancellationToken cancellationToken = default) =>
        SendRequiredAsync<JobResponse>(HttpMethod.Post, $"api/jobs/{jobId:D}/cancel", null, null, cancellationToken);

    private async Task<T> SendRequiredAsync<T>(HttpMethod method, string path, object? body, string? idempotencyKey, CancellationToken cancellationToken) where T : class =>
        (await SendAsync<T>(method, path, body, idempotencyKey, _requestTimeout, allowNoContent: false, cancellationToken).ConfigureAwait(false))!;

    private async Task<T?> SendAsync<T>(HttpMethod method, string path, object? body, string? idempotencyKey, TimeSpan timeout, bool allowNoContent, CancellationToken cancellationToken) where T : class
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_httpClient.Timeout != Timeout.InfiniteTimeSpan && _httpClient.Timeout < timeout)
        {
            throw new InvalidOperationException($"The injected HttpClient.Timeout must be infinite or at least {timeout.TotalSeconds} seconds for this request.");
        }

        using CancellationTokenSource requestTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        requestTimeout.CancelAfter(timeout);
        using HttpRequestMessage request = new(method, new Uri(_baseUrl, path));
        if (body is not null)
        {
            request.Content = JsonContent.Create(body, options: JsonOptions);
        }

        if (idempotencyKey is not null)
        {
            request.Headers.Add("Idempotency-Key", idempotencyKey);
        }

        try
        {
            using HttpResponseMessage response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, requestTimeout.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                string raw = await response.Content.ReadAsStringAsync(requestTimeout.Token).ConfigureAwait(false);
                TaskForgeError? error = null;
                try
                {
                    error = JsonSerializer.Deserialize<TaskForgeError>(raw, JsonOptions);
                }
                catch (JsonException)
                {
                }

                throw new TaskForgeApiException(response.StatusCode, error ?? new TaskForgeError(), raw);
            }

            if (allowNoContent && response.StatusCode == HttpStatusCode.NoContent)
            {
                return null;
            }

            return await response.Content.ReadFromJsonAsync<T>(JsonOptions, requestTimeout.Token).ConfigureAwait(false)
                ?? throw new JsonException("TaskForge returned JSON null where a response body was required.");
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested && requestTimeout.IsCancellationRequested)
        {
            throw new TimeoutException($"The TaskForge request exceeded {timeout.TotalSeconds} seconds.", exception);
        }
    }
}
