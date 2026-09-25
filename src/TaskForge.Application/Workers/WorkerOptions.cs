namespace TaskForge.Application.Workers;

public sealed class WorkerOptions
{
    public const string SectionName = "Worker";

    public int PollIntervalMilliseconds { get; init; } = 500;
    public int RetryDelaySeconds { get; init; } = 5;
    public int MaxRetryDelaySeconds { get; init; } = 300;
    public int LeaseGraceSeconds { get; init; } = 30;
}
