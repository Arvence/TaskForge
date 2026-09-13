using Microsoft.EntityFrameworkCore.Diagnostics;

namespace TaskForge.IntegrationTests;

internal sealed class ConcurrentSaveInterceptor : SaveChangesInterceptor
{
    private readonly TaskCompletionSource _bothReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _arrivals;

    public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        if (Interlocked.Increment(ref _arrivals) == 2)
        {
            _bothReady.TrySetResult();
        }

        await _bothReady.Task.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
        return result;
    }
}
