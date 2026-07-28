using System.Collections.Concurrent;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using TaskForge.Application.Abstractions.Persistence;
using TaskForge.Domain.Workers;

namespace TaskForge.Application.Workers;

public sealed class WorkerManager(
    IServiceScopeFactory serviceScopeFactory,
    TimeProvider timeProvider,
    IOptions<WorkerOptions> options,
    ILogger<WorkerManager> logger)
    : BackgroundService
{
    private readonly ConcurrentDictionary<string, WorkerRuntime> _workers = [];
    private readonly SemaphoreSlim _scaleLock = new(1, 1);
    private readonly WorkerOptions _options = options.Value;
    private CancellationToken _hostStoppingToken;
    private int _desiredWorkerCount;
    private int _nextWorkerNumber;

    public WorkerManagerSnapshot GetSnapshot()
    {
        WorkerSnapshot[] workers = _workers.Values
            .Select(worker => worker.GetSnapshot())
            .OrderBy(worker => worker.Id, StringComparer.Ordinal)
            .ToArray();

        return new WorkerManagerSnapshot(
            Volatile.Read(ref _desiredWorkerCount),
            workers.Count(worker => worker.Status != WorkerStatus.Stopping),
            WorkerOptions.MaximumWorkerCount,
            workers);
    }

    public async Task<WorkerManagerSnapshot> SetWorkerCountAsync(
        int count,
        CancellationToken cancellationToken = default)
    {
        EnsureValidWorkerCount(count);

        await using (AsyncServiceScope scope =
            serviceScopeFactory.CreateAsyncScope())
        {
            IWorkerSettingsStore settingsStore =
                scope.ServiceProvider.GetRequiredService<IWorkerSettingsStore>();
            await settingsStore.SetDesiredWorkerCountAsync(
                count,
                timeProvider.GetUtcNow(),
                cancellationToken);
        }

        Volatile.Write(ref _desiredWorkerCount, count);
        await ReconcileWorkersAsync(cancellationToken);
        return GetSnapshot();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _hostStoppingToken = stoppingToken;
        int desiredWorkerCount = await LoadDesiredWorkerCountAsync(stoppingToken);
        Volatile.Write(ref _desiredWorkerCount, desiredWorkerCount);
        await ReconcileWorkersAsync(stoppingToken);

        logger.LogInformation(
            "Worker manager started with {WorkerCount} worker(s).",
            desiredWorkerCount);

        try
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        finally
        {
            WorkerRuntime[] workers = _workers.Values.ToArray();
            foreach (WorkerRuntime worker in workers)
            {
                worker.RequestStop();
            }

            await Task.WhenAll(workers.Select(worker => worker.Completion));
            logger.LogInformation("Worker manager stopped.");
        }
    }

    private async Task<int> LoadDesiredWorkerCountAsync(
        CancellationToken cancellationToken)
    {
        EnsureValidWorkerCount(_options.Count);

        await using AsyncServiceScope scope =
            serviceScopeFactory.CreateAsyncScope();
        IWorkerSettingsStore settingsStore =
            scope.ServiceProvider.GetRequiredService<IWorkerSettingsStore>();
        int? persistedCount = await settingsStore.GetDesiredWorkerCountAsync(
            cancellationToken);

        if (persistedCount is not null)
        {
            EnsureValidWorkerCount(persistedCount.Value);
            return persistedCount.Value;
        }

        await settingsStore.SetDesiredWorkerCountAsync(
            _options.Count,
            timeProvider.GetUtcNow(),
            cancellationToken);
        return _options.Count;
    }

    private async Task ReconcileWorkersAsync(
        CancellationToken cancellationToken)
    {
        await _scaleLock.WaitAsync(cancellationToken);

        try
        {
            int desiredCount = Volatile.Read(ref _desiredWorkerCount);
            WorkerRuntime[] activeWorkers = _workers.Values
                .Where(worker => !worker.StopRequested)
                .OrderBy(worker => worker.Number)
                .ToArray();

            if (activeWorkers.Length < desiredCount)
            {
                for (int index = activeWorkers.Length; index < desiredCount; index++)
                {
                    StartWorker();
                }

                return;
            }

            foreach (WorkerRuntime worker in activeWorkers
                .OrderByDescending(worker => worker.Number)
                .Take(activeWorkers.Length - desiredCount))
            {
                logger.LogInformation(
                    "Worker {WorkerId} will stop after its current job.",
                    worker.Id);
                worker.RequestStop();
            }
        }
        finally
        {
            _scaleLock.Release();
        }
    }

    private void StartWorker()
    {
        int workerNumber = Interlocked.Increment(ref _nextWorkerNumber);
        string workerId =
            $"{Environment.MachineName}-{Environment.ProcessId}-{workerNumber}";
        WorkerRuntime worker = new(
            workerNumber,
            workerId,
            timeProvider.GetUtcNow());

        if (!_workers.TryAdd(workerId, worker))
        {
            throw new InvalidOperationException(
                $"Worker '{workerId}' is already registered.");
        }

        worker.Completion = RunWorkerAsync(worker, _hostStoppingToken);
    }

    private async Task RunWorkerAsync(
        WorkerRuntime worker,
        CancellationToken hostStoppingToken)
    {
        logger.LogInformation("Worker {WorkerId} started.", worker.Id);
        worker.MarkIdle();

        try
        {
            while (!hostStoppingToken.IsCancellationRequested
                && !worker.StopRequested)
            {
                bool processedJob;

                try
                {
                    await using AsyncServiceScope scope =
                        serviceScopeFactory.CreateAsyncScope();
                    JobExecutor executor =
                        scope.ServiceProvider.GetRequiredService<JobExecutor>();
                    using CancellationTokenSource acquisition =
                        CancellationTokenSource.CreateLinkedTokenSource(
                            hostStoppingToken,
                            worker.StopToken);
                    processedJob = await executor.ProcessNextAsync(
                        worker.Id,
                        worker.SetCurrentJob,
                        acquisition.Token,
                        hostStoppingToken);
                }
                catch (OperationCanceledException)
                    when (hostStoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception exception)
                {
                    processedJob = false;
                    logger.LogError(
                        exception,
                        "Worker {WorkerId} loop failed and will retry.",
                        worker.Id);
                }

                if (worker.StopRequested || hostStoppingToken.IsCancellationRequested)
                {
                    break;
                }

                if (!processedJob)
                {
                    using CancellationTokenSource polling =
                        CancellationTokenSource.CreateLinkedTokenSource(
                            hostStoppingToken,
                            worker.StopToken);

                    try
                    {
                        await Task.Delay(
                            TimeSpan.FromMilliseconds(
                                _options.PollIntervalMilliseconds),
                            polling.Token);
                    }
                    catch (OperationCanceledException)
                        when (polling.IsCancellationRequested)
                    {
                        break;
                    }
                }
            }
        }
        finally
        {
            worker.MarkStopping();
            _workers.TryRemove(worker.Id, out _);
            worker.Dispose();
            logger.LogInformation("Worker {WorkerId} stopped.", worker.Id);
        }
    }

    private static void EnsureValidWorkerCount(int count)
    {
        if (count is < 0 or > WorkerOptions.MaximumWorkerCount)
        {
            throw new ArgumentOutOfRangeException(
                nameof(count),
                $"Worker count must be between 0 and "
                + $"{WorkerOptions.MaximumWorkerCount}.");
        }
    }

    private sealed class WorkerRuntime(
        int number,
        string id,
        DateTimeOffset startedAtUtc)
        : IDisposable
    {
        private readonly object _stateLock = new();
        private readonly CancellationTokenSource _stop = new();
        private WorkerStatus _status = WorkerStatus.Starting;
        private Guid? _currentJobId;
        private bool _stopRequested;
        private bool _disposed;

        public int Number { get; } = number;
        public string Id { get; } = id;
        public DateTimeOffset StartedAtUtc { get; } = startedAtUtc;
        public Task Completion { get; set; } = Task.CompletedTask;
        public bool StopRequested
        {
            get
            {
                lock (_stateLock)
                {
                    return _stopRequested;
                }
            }
        }

        public CancellationToken StopToken => _stop.Token;

        public WorkerSnapshot GetSnapshot()
        {
            lock (_stateLock)
            {
                return new WorkerSnapshot(
                    Id,
                    _status,
                    _currentJobId,
                    StartedAtUtc);
            }
        }

        public void MarkIdle()
        {
            lock (_stateLock)
            {
                if (!_stopRequested)
                {
                    _status = WorkerStatus.Idle;
                }
            }
        }

        public void SetCurrentJob(Guid? jobId)
        {
            lock (_stateLock)
            {
                _currentJobId = jobId;
                _status = _stopRequested
                    ? WorkerStatus.Stopping
                    : jobId is null
                        ? WorkerStatus.Idle
                        : WorkerStatus.Busy;
            }
        }

        public void RequestStop()
        {
            lock (_stateLock)
            {
                if (_disposed || _stopRequested)
                {
                    return;
                }

                _stopRequested = true;
                _status = WorkerStatus.Stopping;
                _stop.Cancel();
            }
        }

        public void MarkStopping()
        {
            lock (_stateLock)
            {
                _status = WorkerStatus.Stopping;
            }
        }

        public void Dispose()
        {
            lock (_stateLock)
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
                _stop.Dispose();
            }
        }
    }
}