namespace TaskForge.Application.Abstractions.Execution;

public interface IJobHandler
{
    string JobType { get; }

    Task<string?> HandleAsync(
        string payloadJson,
        CancellationToken cancellationToken = default);
}