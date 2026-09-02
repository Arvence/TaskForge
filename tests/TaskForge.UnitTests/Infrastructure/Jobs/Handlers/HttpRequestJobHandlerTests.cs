using System.Net;
using System.Text.Json;

using Microsoft.Extensions.Options;

using TaskForge.Application.Abstractions.Execution;
using TaskForge.Infrastructure.Jobs.Handlers;

namespace TaskForge.UnitTests.Infrastructure.Jobs.Handlers;

public sealed class HttpRequestJobHandlerTests
{
    [Fact]
    public async Task Allowed_request_returns_compact_http_result()
    {
        StubHttpMessageHandler messageHandler = new(
            new HttpResponseMessage(HttpStatusCode.Created)
            {
                ReasonPhrase = "Created"
            });
        HttpRequestJobHandler handler = CreateHandler(
            messageHandler,
            ["api.example.test"]);

        string? resultJson = await handler.HandleAsync(
            """
            {
              "url": "https://api.example.test/tasks",
              "method": "POST",
              "body": { "taskId": 42 },
              "headers": { "X-Source": "TaskForge" }
            }
            """);

        using JsonDocument result = JsonDocument.Parse(resultJson!);
        Assert.Equal(201, result.RootElement.GetProperty("statusCode").GetInt32());
        Assert.Equal(HttpMethod.Post, messageHandler.Method);
        Assert.Equal(
            "https://api.example.test/tasks",
            messageHandler.RequestUri!.AbsoluteUri);
        using JsonDocument requestBody = JsonDocument.Parse(messageHandler.Body!);
        Assert.Equal(
            42,
            requestBody.RootElement.GetProperty("taskId").GetInt32());
        Assert.Equal("TaskForge", messageHandler.SourceHeader);
    }

    [Fact]
    public void Valid_payload_is_accepted_without_sending()
    {
        StubHttpMessageHandler messageHandler = new(
            new HttpResponseMessage(HttpStatusCode.OK));
        HttpRequestJobHandler handler = CreateHandler(
            messageHandler,
            ["api.example.test"]);

        string? error = handler.ValidatePayload(
            """
            {
              "url": "https://api.example.test/tasks",
              "method": "POST"
            }
            """);

        Assert.Null(error);
        Assert.Equal(0, messageHandler.CallCount);
    }

    [Theory]
    [InlineData(
        """{"url":"https://api.example.test/tasks","method":"TRACE"}""",
        "not supported")]
    [InlineData(
        """{"url":"https://internal.example.test/tasks","method":"POST"}""",
        "not allowed")]
    [InlineData("""{""", "invalid")]
    public void Invalid_payload_is_rejected_without_sending(
        string payloadJson,
        string expectedError)
    {
        StubHttpMessageHandler messageHandler = new(
            new HttpResponseMessage(HttpStatusCode.OK));
        HttpRequestJobHandler handler = CreateHandler(
            messageHandler,
            ["api.example.test"]);

        string? error = handler.ValidatePayload(payloadJson);

        Assert.NotNull(error);
        Assert.Contains(expectedError, error, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, messageHandler.CallCount);
    }

    [Fact]
    public async Task Host_outside_allowlist_is_rejected_before_sending()
    {
        StubHttpMessageHandler messageHandler = new(
            new HttpResponseMessage(HttpStatusCode.OK));
        HttpRequestJobHandler handler = CreateHandler(
            messageHandler,
            ["api.example.test"]);

        NonRetryableJobException exception =
            await Assert.ThrowsAsync<NonRetryableJobException>(
                () => handler.HandleAsync(
                    """
                    {
                      "url": "https://internal.example.test/tasks",
                      "method": "POST"
                    }
                    """));

        Assert.Contains("not allowed", exception.Message);
        Assert.Equal(0, messageHandler.CallCount);
    }

    [Theory]
    [InlineData(HttpStatusCode.RequestTimeout)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.BadGateway)]
    public async Task Retryable_status_is_reported_as_transient_failure(HttpStatusCode statusCode)
    {
        StubHttpMessageHandler messageHandler = new(
            new HttpResponseMessage(statusCode));
        HttpRequestJobHandler handler = CreateHandler(
            messageHandler,
            ["api.example.test"]);

        HttpRequestException exception =
            await Assert.ThrowsAsync<HttpRequestException>(
                () => handler.HandleAsync(
                    """
                    {
                      "url": "https://api.example.test/tasks",
                      "method": "GET"
                    }
                    """));

        Assert.Equal(statusCode, exception.StatusCode);
    }

    [Theory]
    [InlineData(HttpStatusCode.MultipleChoices)]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.Conflict)]
    [InlineData(HttpStatusCode.UnprocessableEntity)]
    public async Task Permanent_status_is_reported_as_non_retryable_failure(HttpStatusCode statusCode)
    {
        StubHttpMessageHandler messageHandler = new(
            new HttpResponseMessage(statusCode));
        HttpRequestJobHandler handler = CreateHandler(
            messageHandler,
            ["api.example.test"]);

        NonRetryableJobException exception =
            await Assert.ThrowsAsync<NonRetryableJobException>(
                () => handler.HandleAsync(
                    """
                    {
                      "url": "https://api.example.test/tasks",
                      "method": "GET"
                    }
                    """));

        HttpRequestException innerException =
            Assert.IsType<HttpRequestException>(exception.InnerException);
        Assert.Equal(statusCode, innerException.StatusCode);
    }

    [Fact]
    public async Task Invalid_payload_is_reported_as_non_retryable_failure()
    {
        StubHttpMessageHandler messageHandler = new(
            new HttpResponseMessage(HttpStatusCode.OK));
        HttpRequestJobHandler handler = CreateHandler(
            messageHandler,
            ["api.example.test"]);

        NonRetryableJobException exception =
            await Assert.ThrowsAsync<NonRetryableJobException>(
                () => handler.HandleAsync("""{"""));

        Assert.Contains("invalid", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, messageHandler.CallCount);
    }

    private static HttpRequestJobHandler CreateHandler(
        HttpMessageHandler messageHandler,
        string[] allowedHosts) =>
        new(
            new HttpClient(messageHandler),
            Options.Create(new HttpRequestJobOptions
            {
                AllowedHosts = allowedHosts
            }));

    private sealed class StubHttpMessageHandler(HttpResponseMessage response)
        : HttpMessageHandler
    {
        public int CallCount { get; private set; }
        public HttpMethod? Method { get; private set; }
        public Uri? RequestUri { get; private set; }
        public string? Body { get; private set; }
        public string? SourceHeader { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            CallCount++;
            Method = request.Method;
            RequestUri = request.RequestUri;
            Body = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);
            SourceHeader = request.Headers.TryGetValues(
                "X-Source",
                out IEnumerable<string>? values)
                ? values.Single()
                : null;
            return response;
        }
    }
}
