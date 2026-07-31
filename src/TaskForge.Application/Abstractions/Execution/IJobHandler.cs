namespace TaskForge.Application.Abstractions.Execution;

public interface IJobHandler
{
    string JobType { get; }

    string? ValidatePayload(string payloadJson);

    Task<string?> HandleAsync(
        string payloadJson,
        CancellationToken cancellationToken = default);
}