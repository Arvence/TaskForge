using System.Net;
using System.Text.Json;

using Microsoft.Extensions.Options;

using TaskForge.SampleClient.Handlers;
using TaskForge.SDK.Handlers;

namespace TaskForge.UnitTests.SampleClient;

public sealed class HttpRequestJobHandlerTests
{
    [Fact]
    public async Task Allowed_request_sends_json_and_headers_and_returns_compact_metadata()
    {
        using StubHttpHandler transport = new(HttpStatusCode.Created);
        using HttpClient http = new(transport);
        HttpRequestPayload payload = new("https://api.example.test/tasks", "POST", JsonSerializer.SerializeToElement(new { taskId = 42 }), new Dictionary<string, string?> { ["X-Source"] = "SampleClient" });
        JsonElement result = (await Handler(http).HandleAsync(payload))!.Value;
        Assert.Equal(201, result.GetProperty("statusCode").GetInt32());
        Assert.Equal("Created", result.GetProperty("reasonPhrase").GetString());
        Assert.Equal(HttpMethod.Post, transport.Method);
        Assert.Equal("https://api.example.test/tasks", transport.Uri!.AbsoluteUri);
        Assert.Equal("SampleClient", transport.Source);
        using JsonDocument body = JsonDocument.Parse(transport.Body!);
        Assert.Equal(42, body.RootElement.GetProperty("taskId").GetInt32());
        Assert.InRange(result.GetRawText().Length, 1, 4000);
        Assert.Equal(1, transport.Calls);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(" ")]
    [InlineData("post")]
    public async Task Missing_or_lowercase_method_uses_post(string? method)
    {
        using StubHttpHandler transport = new(HttpStatusCode.OK);
        using HttpClient http = new(transport);
        await Handler(http).HandleAsync(new("https://API.EXAMPLE.TEST/tasks", method));
        Assert.Equal(HttpMethod.Post, transport.Method);
        Assert.Equal(1, transport.Calls);
    }

    [Theory]
    [InlineData("https://api.example.test/tasks", "TRACE", "not supported")]
    [InlineData("https://internal.example.test/tasks", "GET", "not allowed")]
    [InlineData("https://api.example.test.evil.test/tasks", "GET", "not allowed")]
    [InlineData("file:///tmp/data", "GET", "absolute HTTP")]
    [InlineData("/tasks", "GET", "absolute HTTP")]
    [InlineData("https://user:password@api.example.test/tasks", "GET", "user information")]
    [InlineData(null, "GET", "absolute HTTP")]
    public async Task Invalid_requests_fail_permanently_before_sending(string? url, string method, string message)
    {
        using StubHttpHandler transport = new(HttpStatusCode.OK);
        using HttpClient http = new(transport);
        NonRetryableJobException error = await Assert.ThrowsAsync<NonRetryableJobException>(() => Handler(http).HandleAsync(new(url, method)));
        Assert.Contains(message, error.Message);
        Assert.Equal(0, transport.Calls);
    }

    [Theory]
    [InlineData("Host", "another.test")]
    [InlineData("Content-Length", "5")]
    [InlineData("Connection", "close")]
    [InlineData("Transfer-Encoding", "chunked")]
    [InlineData("Invalid Header", "value")]
    [InlineData("X-Test", "value\r\ninjected: value")]
    [InlineData("X-Test", null)]
    public async Task Invalid_headers_fail_before_sending(string name, string? value)
    {
        using StubHttpHandler transport = new(HttpStatusCode.OK);
        using HttpClient http = new(transport);
        await Assert.ThrowsAsync<NonRetryableJobException>(() => Handler(http).HandleAsync(new("https://api.example.test/tasks", Headers: new Dictionary<string, string?> { [name] = value })));
        Assert.Equal(0, transport.Calls);
    }

    [Theory]
    [InlineData(HttpStatusCode.RequestTimeout)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.BadGateway)]
    public async Task Transient_http_failure_is_reported_once_for_server_owned_retry(HttpStatusCode status)
    {
        using StubHttpHandler transport = new(status);
        using HttpClient http = new(transport);
        HttpRequestException error = await Assert.ThrowsAsync<HttpRequestException>(() => Handler(http).HandleAsync(new("https://api.example.test/tasks", "GET")));
        Assert.Equal(status, error.StatusCode);
        Assert.Equal(1, transport.Calls);
    }

    [Theory]
    [InlineData(HttpStatusCode.MultipleChoices)]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.Conflict)]
    [InlineData(HttpStatusCode.UnprocessableEntity)]
    public async Task Permanent_http_failure_keeps_its_original_status(HttpStatusCode status)
    {
        using StubHttpHandler transport = new(status);
        using HttpClient http = new(transport);
        NonRetryableJobException error = await Assert.ThrowsAsync<NonRetryableJobException>(() => Handler(http).HandleAsync(new("https://api.example.test/tasks", "GET")));
        Assert.Equal(status, Assert.IsType<HttpRequestException>(error.InnerException).StatusCode);
        Assert.Equal(1, transport.Calls);
    }

    [Fact]
    public async Task Cancellation_reaches_the_business_http_request()
    {
        using CancellationTokenSource cancellation = new();
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using StubHttpHandler transport = new(HttpStatusCode.OK)
        {
            SendResponse = async token =>
            {
                entered.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                return new(HttpStatusCode.OK);
            }
        };
        using HttpClient http = new(transport);
        Task<JsonElement?> request = Handler(http).HandleAsync(new("https://api.example.test/tasks"), cancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);
        Assert.Equal(1, transport.Calls);
    }

    private static HttpRequestJobHandler Handler(HttpClient http) => new(http, Options.Create(new HttpRequestJobOptions { AllowedHosts = ["api.example.test"] }));

    private sealed class StubHttpHandler(HttpStatusCode status) : HttpMessageHandler
    {
        public int Calls;
        public HttpMethod? Method;
        public Uri? Uri;
        public string? Body;
        public string? Source;
        public Func<CancellationToken, Task<HttpResponseMessage>>? SendResponse;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            Method = request.Method;
            Uri = request.RequestUri;
            Body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            Source = request.Headers.TryGetValues("X-Source", out IEnumerable<string>? values) ? values.Single() : null;
            return SendResponse is null ? new(status) : await SendResponse(cancellationToken);
        }
    }
}
