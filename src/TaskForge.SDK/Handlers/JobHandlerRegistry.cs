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
    private readonly Func<JsonElement, (string Operation, JsonElement Parameters)>? _selectOperation;
    private readonly JsonSerializerOptions? _parameterOptions;

    internal JobHandlerRegistry(IServiceScopeFactory scopeFactory, FrozenDictionary<string, JobHandlerAdapter> handlers, string[]? supportedTypes, Func<JsonElement, (string Operation, JsonElement Parameters)>? selectOperation, JsonSerializerOptions? parameterOptions)
    {
        _scopeFactory = scopeFactory;
        _handlers = handlers;
        _selectOperation = selectOperation;
        _parameterOptions = parameterOptions;
        IEnumerable<string> types = handlers.Count == 0 ? [] : supportedTypes ?? (IEnumerable<string>)handlers.Keys;
        RegisteredTypes = Array.AsReadOnly(types.Order(StringComparer.OrdinalIgnoreCase).ToArray());
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
        if (_selectOperation is not null)
        {
            if (!RegisteredTypes.Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                throw new JobHandlerNotFoundException(name);
            }

            (name, payload) = _selectOperation(payload);
        }

        if (!_handlers.TryGetValue(name, out JobHandlerAdapter? handler))
        {
            throw _selectOperation is null
                ? new JobHandlerNotFoundException(name)
                : new NonRetryableJobException("The requested operation is not registered.");
        }

        return handler.ExecuteAsync(_scopeFactory, payload, context, cancellationToken, _parameterOptions);
    }
}
