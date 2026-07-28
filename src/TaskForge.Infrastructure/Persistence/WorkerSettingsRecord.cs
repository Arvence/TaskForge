namespace TaskForge.Infrastructure.Persistence;

internal sealed class WorkerSettingsRecord
{
    public const int SingletonId = 1;

    public int Id { get; set; } = SingletonId;
    public int DesiredWorkerCount { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
}