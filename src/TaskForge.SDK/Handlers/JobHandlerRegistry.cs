using System.Collections.Frozen;
using System.Text.Json;

using Microsoft.Extensions.DependencyInjection;

namespace TaskForge.SDK.Handlers;

public sealed class JobHandlerRegistry
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly FrozenDictionary<string, JobHandlerAdapter> _handlers;

    internal JobHandlerRegistry(IServiceScopeFactory scopeFactory, FrozenDictionary<string, JobHandlerAdapter> handlers)
    {
        _scopeFactory = scopeFactory;
        _handlers = handlers;
        RegisteredTypes = Array.AsReadOnly(handlers.Keys.Order(StringComparer.OrdinalIgnoreCase).ToArray());
    }

    public IReadOnlyList<string> RegisteredTypes { get; }

    public Task<JsonElement?> ExecuteAsync(string jobType, JsonElement payload, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobType);
        cancellationToken.ThrowIfCancellationRequested();
        string name = jobType.Trim();
        if (!_handlers.TryGetValue(name, out JobHandlerAdapter? handler))
        {
            throw new JobHandlerNotFoundException(name);
        }

        return handler.ExecuteAsync(_scopeFactory, payload, cancellationToken);
    }
}
