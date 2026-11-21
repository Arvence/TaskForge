using System.Net;
using System.Text.Json;

using Microsoft.Extensions.Options;

using TaskForge.SampleClient.Handlers;
using TaskForge.SDK.Handlers;

namespace TaskForge.UnitTests.SampleClient;

public sealed class HttpRequestJobHandlerTests
{
    [Fact]
    public async Task Allowed_get_sends_no_body_or_headers_and_returns_compact_metadata()
    {
        using StubHttpHandler transport = new(HttpStatusCode.Created);
        using HttpClient http = new(transport);
        using JsonDocument payload = JsonDocument.Parse("""{"url":"https://api.example.test/tasks","method":"GET","body":null,"headers":null}""");
        JsonElement result = (await ((IJobHandler<JsonElement>)Handler(http)).HandleAsync(payload.RootElement))!.Value;
        Assert.Equal(201, result.GetProperty("statusCode").GetInt32());
        Assert.Equal("Created", result.GetProperty("reasonPhrase").GetString());
        Assert.Equal(HttpMethod.Get, transport.Method);
        Assert.Equal("https://api.example.test/tasks", transport.Uri!.AbsoluteUri);
        Assert.False(transport.HasContent);
        Assert.False(transport.HasHeaders);
        Assert.InRange(result.GetRawText().Length, 1, 4000);
        Assert.Equal(1, transport.Calls);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(" ")]
    [InlineData("GET ")]
    [InlineData("get")]
    [InlineData("POST")]
    [InlineData("PUT")]
    [InlineData("PATCH")]
    [InlineData("DELETE")]
    [InlineData("TRACE")]
    public async Task Requests_without_an_explicit_get_fail_before_sending(string? method)
    {
        using StubHttpHandler transport = new(HttpStatusCode.OK);
        using HttpClient http = new(transport);
        NonRetryableJobException error = await Assert.ThrowsAsync<NonRetryableJobException>(
            () => Handler(http).HandleAsync(new("https://api.example.test/tasks", method)));
        Assert.Contains("explicit GET", error.Message);
        Assert.Equal(0, transport.Calls);
    }

    [Theory]
    [InlineData("https://internal.example.test/tasks", "GET", "not allowed")]
    [InlineData("https://api.example.test.evil.test/tasks", "GET", "not allowed")]
    [InlineData("http://api.example.test/tasks", "GET", "not allowed")]
    [InlineData("https://api.example.test:8443/tasks", "GET", "not allowed")]
    [InlineData("https://api.example.test/tasks?value=1", "GET", "query or fragment")]
    [InlineData("https://api.example.test/tasks#section", "GET", "query or fragment")]
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
    [InlineData("""{"url":"https://api.example.test/tasks","method":"GET","extra":true}""")]
    [InlineData("""{"Url":"https://api.example.test/tasks","method":"GET"}""")]
    [InlineData("""{"url":"https://api.example.test/tasks","method":"POST","method":"GET"}""")]
    [InlineData("""{"url":"https://api.example.test/tasks","method":1}""")]
    public async Task Invalid_payload_fields_fail_before_sending(string json)
    {
        using StubHttpHandler transport = new(HttpStatusCode.OK);
        using HttpClient http = new(transport);
        using JsonDocument payload = JsonDocument.Parse(json);
        IJobHandler<JsonElement> handler = Handler(http);
        await Assert.ThrowsAsync<InvalidJobPayloadException>(async () => await handler.HandleAsync(payload.RootElement));
        Assert.Equal(0, transport.Calls);
    }

    [Fact]
    public async Task Get_url_is_bounded_before_sending()
    {
        const string prefix = "https://api.example.test/";
        string maximum = prefix + new string('x', 2048 - prefix.Length);
        using StubHttpHandler transport = new(HttpStatusCode.OK);
        using HttpClient http = new(transport);
        HttpRequestJobHandler handler = Handler(http);
        NonRetryableJobException error = await Assert.ThrowsAsync<NonRetryableJobException>(() => handler.HandleAsync(new(maximum + "x", "GET")));
        Assert.Contains("2048", error.Message);
        Assert.Equal(0, transport.Calls);

        await handler.HandleAsync(new(maximum, "GET"));
        Assert.Equal(1, transport.Calls);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Get_jobs_reject_a_body_or_headers_before_sending(bool hasBody)
    {
        using StubHttpHandler transport = new(HttpStatusCode.OK);
        using HttpClient http = new(transport);
        HttpRequestPayload request = hasBody
            ? new("https://api.example.test/tasks", "GET", JsonSerializer.SerializeToElement(new { value = 42 }))
            : new("https://api.example.test/tasks", "GET", Headers: new Dictionary<string, string?> { ["X-Source"] = "SampleClient" });

        await Assert.ThrowsAsync<NonRetryableJobException>(() => Handler(http).HandleAsync(request));
        Assert.Equal(0, transport.Calls);
    }

    [Fact]
    public async Task Http_execution_requires_an_explicit_worker_origin()
    {
        using StubHttpHandler transport = new(HttpStatusCode.OK);
        using HttpClient http = new(transport);
        HttpRequestJobHandler handler = new(http, Options.Create(new HttpRequestJobOptions()));

        await Assert.ThrowsAsync<NonRetryableJobException>(() => handler.HandleAsync(new("http://localhost:8275/api/health", "GET")));
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
        Task<JsonElement?> request = Handler(http).HandleAsync(new("https://api.example.test/tasks", "GET"), cancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);
        Assert.Equal(1, transport.Calls);
    }

    private static HttpRequestJobHandler Handler(HttpClient http) => new(http, Options.Create(new HttpRequestJobOptions { AllowedOrigin = "https://api.example.test" }));

    private sealed class StubHttpHandler(HttpStatusCode status) : HttpMessageHandler
    {
        public int Calls;
        public HttpMethod? Method;
        public Uri? Uri;
        public bool HasContent;
        public bool HasHeaders;
        public Func<CancellationToken, Task<HttpResponseMessage>>? SendResponse;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            Method = request.Method;
            Uri = request.RequestUri;
            HasContent = request.Content is not null;
            HasHeaders = request.Headers.Any();
            return SendResponse is null ? new(status) : await SendResponse(cancellationToken);
        }
    }
}
