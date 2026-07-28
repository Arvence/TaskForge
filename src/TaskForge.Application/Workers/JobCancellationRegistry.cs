using System.Collections.Concurrent;

namespace TaskForge.Application.Workers;

public sealed class JobCancellationRegistry
{
    private readonly ConcurrentDictionary<Guid, CancellationEntry> _entries = [];

    public Registration Register(Guid jobId)
    {
        CancellationEntry entry = new();
        if (!_entries.TryAdd(jobId, entry))
        {
            entry.Dispose();
            throw new InvalidOperationException(
                $"Job '{jobId}' is already registered for execution.");
        }

        return new Registration(this, jobId, entry);
    }

    public bool Cancel(Guid jobId) =>
        _entries.TryGetValue(jobId, out CancellationEntry? entry)
        && entry.Cancel();

    private void Unregister(Guid jobId, CancellationEntry entry)
    {
        if (_entries.TryRemove(jobId, out CancellationEntry? removed))
        {
            removed.Dispose();
        }
        else
        {
            entry.Dispose();
        }
    }

    public sealed class Registration : IDisposable
    {
        private readonly JobCancellationRegistry _registry;
        private readonly Guid _jobId;
        private CancellationEntry? _entry;

        internal Registration(
            JobCancellationRegistry registry,
            Guid jobId,
            CancellationEntry entry)
        {
            _registry = registry;
            _jobId = jobId;
            _entry = entry;
        }

        public CancellationToken Token =>
            _entry?.Token ?? CancellationToken.None;

        public void Dispose()
        {
            CancellationEntry? entry = Interlocked.Exchange(ref _entry, null);
            if (entry is not null)
            {
                _registry.Unregister(_jobId, entry);
            }
        }
    }

    internal sealed class CancellationEntry : IDisposable
    {
        private readonly object _lock = new();
        private readonly CancellationTokenSource _source = new();
        private bool _disposed;

        public CancellationToken Token => _source.Token;

        public bool Cancel()
        {
            lock (_lock)
            {
                if (_disposed)
                {
                    return false;
                }

                _source.Cancel();
                return true;
            }
        }

        public void Dispose()
        {
            lock (_lock)
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
                _source.Dispose();
            }
        }
    }
}