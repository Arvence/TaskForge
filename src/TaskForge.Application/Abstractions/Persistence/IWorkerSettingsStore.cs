namespace TaskForge.Application.Abstractions.Persistence;

public interface IWorkerSettingsStore
{
    Task<int?> GetDesiredWorkerCountAsync(
        CancellationToken cancellationToken = default);

    Task SetDesiredWorkerCountAsync(
        int count,
        DateTimeOffset updatedAtUtc,
        CancellationToken cancellationToken = default);
}