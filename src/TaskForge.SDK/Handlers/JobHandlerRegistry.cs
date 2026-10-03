using System.Collections.Frozen;
using System.Text.Json;

using Microsoft.Extensions.DependencyInjection;

using TaskForge.SDK.Execution;
using TaskForge.SDK.Jobs;

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

    public Task<JsonElement?> ExecuteAsync(ExecutionAssignmentResponse assignment, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(assignment);
        JobExecutionContext context = new(assignment.JobId, assignment.ApplicationId, assignment.AttemptId, assignment.AttemptNumber,
            assignment.WorkerId, assignment.Type, assignment.StartedAtUtc, assignment.DeadlineAtUtc);
        return ExecuteAsync(assignment.Type, assignment.Payload, context, cancellationToken);
    }

    public Task<JsonElement?> ExecuteAsync(string jobType, JsonElement payload, CancellationToken cancellationToken = default) => ExecuteAsync(jobType, payload, null, cancellationToken);

    private Task<JsonElement?> ExecuteAsync(string jobType, JsonElement payload, JobExecutionContext? context, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobType);
        cancellationToken.ThrowIfCancellationRequested();
        string name = jobType.Trim();
        if (!_handlers.TryGetValue(name, out JobHandlerAdapter? handler))
        {
            throw new JobHandlerNotFoundException(name);
        }

        return handler.ExecuteAsync(_scopeFactory, payload, context, cancellationToken);
    }
}
