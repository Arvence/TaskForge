namespace TaskForge.Application.Abstractions.Execution;

public interface IJobHandler
{
    string JobType { get; }

    Task HandleAsync(
        string payloadJson,
        CancellationToken cancellationToken = default);
}
