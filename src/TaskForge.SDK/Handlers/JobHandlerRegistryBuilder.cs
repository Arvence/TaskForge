using System.Collections.Frozen;

namespace TaskForge.SDK.Handlers;

public sealed class JobHandlerRegistryBuilder
{
    private readonly Dictionary<string, JobHandlerAdapter> _handlers;
    private readonly bool _routeByOperation;
    private bool _frozen;

    internal JobHandlerRegistryBuilder(bool routeByOperation)
    {
        _routeByOperation = routeByOperation;
        _handlers = new(routeByOperation ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase);
    }

    public JobHandlerRegistryBuilder Register<TPayload, THandler>(string jobType) where TPayload : notnull where THandler : class, IJobHandler<TPayload>
    {
        if (_frozen)
        {
            throw new InvalidOperationException("Handler mappings are frozen after startup registration completes.");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(jobType);
        string name = _routeByOperation ? jobType : NormalizeJobType(jobType);

        if (typeof(THandler).IsAbstract)
        {
            throw new ArgumentException($"Handler '{typeof(THandler)}' must be a concrete type.", nameof(THandler));
        }

        if (!_handlers.TryAdd(name, new JobHandlerAdapter<TPayload, THandler>(name)))
        {
            throw new InvalidOperationException(_routeByOperation
                ? $"Operation '{name}' already has a handler mapping."
                : $"Job type '{name}' already has a handler mapping. Type matching is case-insensitive.");
        }

        return this;
    }

    internal FrozenDictionary<string, JobHandlerAdapter> Freeze()
    {
        _frozen = true;
        return _handlers.ToFrozenDictionary(_handlers.Comparer);
    }

    internal static string NormalizeJobType(string jobType)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobType);
        string name = jobType.Trim();
        if (name.Length > 100)
        {
            throw new ArgumentException("Job type cannot exceed 100 characters.", nameof(jobType));
        }

        return name;
    }
}
