namespace TaskForge.SDK.Handlers;

public sealed class JobHandlerNotFoundException(string jobType) : KeyNotFoundException($"No client handler is registered for job type '{jobType}'.")
{
    public string JobType { get; } = jobType;
}
