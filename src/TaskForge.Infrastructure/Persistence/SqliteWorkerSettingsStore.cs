using Microsoft.EntityFrameworkCore;

using TaskForge.Application.Abstractions.Persistence;

namespace TaskForge.Infrastructure.Persistence;

public sealed class SqliteWorkerSettingsStore(TaskForgeDbContext dbContext)
    : IWorkerSettingsStore
{
    public Task<int?> GetDesiredWorkerCountAsync(
        CancellationToken cancellationToken = default) =>
        dbContext.WorkerSettings
            .AsNoTracking()
            .Where(settings => settings.Id == WorkerSettingsRecord.SingletonId)
            .Select(settings => (int?)settings.DesiredWorkerCount)
            .SingleOrDefaultAsync(cancellationToken);

    public async Task SetDesiredWorkerCountAsync(
        int count,
        DateTimeOffset updatedAtUtc,
        CancellationToken cancellationToken = default)
    {
        WorkerSettingsRecord? settings = await dbContext.WorkerSettings
            .SingleOrDefaultAsync(
                candidate => candidate.Id == WorkerSettingsRecord.SingletonId,
                cancellationToken);

        if (settings is null)
        {
            settings = new WorkerSettingsRecord();
            dbContext.WorkerSettings.Add(settings);
        }

        settings.DesiredWorkerCount = count;
        settings.UpdatedAtUtc = updatedAtUtc;
        await dbContext.SaveChangesAsync(cancellationToken);
    }
}