using System.Collections.Frozen;

namespace TaskForge.SDK.Handlers;

public sealed class JobHandlerRegistryBuilder
{
    private readonly Dictionary<string, JobHandlerAdapter> _handlers = new(StringComparer.OrdinalIgnoreCase);
    private bool _frozen;

    internal JobHandlerRegistryBuilder() { }

    public JobHandlerRegistryBuilder Register<TPayload, THandler>(string jobType) where TPayload : notnull where THandler : class, IJobHandler<TPayload>
    {
        if (_frozen)
        {
            throw new InvalidOperationException("Handler mappings are frozen after startup registration completes.");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(jobType);
        string name = jobType.Trim();
        if (name.Length > 100)
        {
            throw new ArgumentException("Job type cannot exceed 100 characters.", nameof(jobType));
        }

        if (typeof(THandler).IsAbstract)
        {
            throw new ArgumentException($"Handler '{typeof(THandler)}' must be a concrete type.", nameof(THandler));
        }

        if (!_handlers.TryAdd(name, new JobHandlerAdapter<TPayload, THandler>(name)))
        {
            throw new InvalidOperationException($"Job type '{name}' already has a handler mapping. Type matching is case-insensitive.");
        }

        return this;
    }

    internal FrozenDictionary<string, JobHandlerAdapter> Freeze()
    {
        _frozen = true;
        return _handlers.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
    }
}
