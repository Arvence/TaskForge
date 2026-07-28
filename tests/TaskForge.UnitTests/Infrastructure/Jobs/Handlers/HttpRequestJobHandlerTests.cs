using System.Net;
using System.Text.Json;

using Microsoft.Extensions.Options;

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
    public async Task Host_outside_allowlist_is_rejected_before_sending()
    {
        StubHttpMessageHandler messageHandler = new(
            new HttpResponseMessage(HttpStatusCode.OK));
        HttpRequestJobHandler handler = CreateHandler(
            messageHandler,
            ["api.example.test"]);

        InvalidOperationException exception =
            await Assert.ThrowsAsync<InvalidOperationException>(
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

    [Fact]
    public async Task Non_success_status_is_reported_as_failure()
    {
        StubHttpMessageHandler messageHandler = new(
            new HttpResponseMessage(HttpStatusCode.BadGateway));
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

        Assert.Equal(HttpStatusCode.BadGateway, exception.StatusCode);
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