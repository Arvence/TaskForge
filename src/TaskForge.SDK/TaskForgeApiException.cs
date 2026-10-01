using System.Net;

namespace TaskForge.SDK;

public sealed class TaskForgeApiException : HttpRequestException
{
    public TaskForgeApiException(HttpStatusCode statusCode, TaskForgeError error, string responseBody) : base(error.Detail ?? error.Message ?? error.Title ?? $"TaskForge returned HTTP {(int)statusCode} ({statusCode}).", null, statusCode)
    {
        Error = error;
        ResponseBody = responseBody;
    }

    public TaskForgeError Error { get; }
    public string ResponseBody { get; }
}
