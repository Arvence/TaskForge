using System.Net;
using System.Text.Json;

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using TaskForge.SDK.Handlers;
using TaskForge.SDK.Jobs;

namespace TaskForge.SDK.Execution;

public sealed class TaskForgeWorker : BackgroundService
{
    private readonly TaskForgeClient _client;
    private readonly JobHandlerRegistry _registry;
    private readonly TaskForgeWorkerOptions _options;
    private readonly ILogger<TaskForgeWorker> _logger;

    public TaskForgeWorker(TaskForgeClient client, JobHandlerRegistry registry, TaskForgeWorkerOptions options, ILogger<TaskForgeWorker>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        if (registry.RegisteredTypes.Count is < 1 or > 100)
        {
            throw new ArgumentException("The worker requires 1 to 100 registered job types.", nameof(registry));
        }

        _client = client;
        _registry = registry;
        _options = options;
        _logger = logger ?? NullLogger<TaskForgeWorker>.Instance;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        string sessionId = Guid.NewGuid().ToString("N");
        using CancellationTokenSource slots = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        await Task.WhenAll(Enumerable.Range(1, _options.SlotCount).Select(slot => RunSlotAsync($"taskforge-{sessionId}-{slot}", slots))).ConfigureAwait(false);
    }

    private async Task RunSlotAsync(string workerId, CancellationTokenSource slots)
    {
        CancellationToken stoppingToken = slots.Token;
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    ExecutionAssignmentResponse? assignment = await _client.WaitAsync(new(workerId, _registry.RegisteredTypes, _options.WaitSeconds), stoppingToken).ConfigureAwait(false);
                    if (assignment is null)
                    {
                        await Task.Delay(_options.NoWorkDelay, stoppingToken).ConfigureAwait(false);
                        continue;
                    }

                    ValidateAssignment(assignment, workerId);
                    await ExecuteAssignmentAsync(assignment, stoppingToken).ConfigureAwait(false);
                }
                catch (Exception exception) when (IsTransportFailure(exception))
                {
                    _logger.LogWarning(exception, "TaskForge communication failed for worker {WorkerId}. Waiting again after a transport delay.", workerId);
                    await Task.Delay(_options.TransportErrorDelay, stoppingToken).ConfigureAwait(false);
                }
            }
        }
        catch
        {
            await slots.CancelAsync().ConfigureAwait(false);
            throw;
        }
    }

    private void ValidateAssignment(ExecutionAssignmentResponse assignment, string workerId)
    {
        if (!string.Equals(assignment.ApplicationId, _client.ApplicationId, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(assignment.WorkerId, workerId, StringComparison.Ordinal)
            || assignment.JobId == Guid.Empty || assignment.AttemptId == Guid.Empty || assignment.AttemptNumber < 1
            || string.IsNullOrWhiteSpace(assignment.Type))
        {
            throw new InvalidOperationException("TaskForge returned an invalid assignment identity. No handler was invoked and no outcome was reported.");
        }
    }

    private async Task ExecuteAssignmentAsync(ExecutionAssignmentResponse assignment, CancellationToken stoppingToken)
    {
        JsonElement? result = null;
        FailExecutionRequest? failure = null;
        try
        {
            result = await _registry.ExecuteAsync(assignment.Type, assignment.Payload, stoppingToken).ConfigureAwait(false);
            ValidateResult(result);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            string code = exception switch
            {
                InvalidJobPayloadException => "InvalidPayload",
                JobHandlerNotFoundException => "UnsupportedJobType",
                JobResultSerializationException => "ResultSerializationFailed",
                _ => exception.GetType().Name
            };
            string message = string.IsNullOrWhiteSpace(exception.Message) ? "The client execution failed." : exception.Message.Trim();
            failure = new(assignment.WorkerId, code.Length > 100 ? code[..100] : code, message.Length > 4000 ? message[..4000] : message);
        }
        stoppingToken.ThrowIfCancellationRequested();
        try
        {
            if (failure is null)
            {
                await _client.CompleteAsync(assignment.JobId, assignment.AttemptId, new(assignment.WorkerId, result), stoppingToken).ConfigureAwait(false);
            }
            else
            {
                await _client.FailAsync(assignment.JobId, assignment.AttemptId, failure, stoppingToken).ConfigureAwait(false);
            }
        }
        catch (TaskForgeApiException exception) when (exception.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Conflict)
        {
            _logger.LogWarning(exception, "TaskForge rejected the report for job {JobId}, attempt {AttemptId}. Waiting for a new assignment.", assignment.JobId, assignment.AttemptId);
        }
    }

    private static void ValidateResult(JsonElement? result)
    {
        if (result is null || result.Value.ValueKind == JsonValueKind.Null)
        {
            return;
        }

        string json;
        try
        {
            json = JsonSerializer.Serialize(result.Value);
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException or InvalidOperationException)
        {
            throw new JobResultSerializationException("The handler result could not be serialized as JSON.", exception);
        }

        if (json.Length > 4000)
        {
            throw new JobResultSerializationException("Serialized result cannot exceed 4000 characters.");
        }
    }

    private static bool IsTransportFailure(Exception exception) => exception switch
    {
        TaskForgeApiException api => api.StatusCode is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests || (int?)api.StatusCode >= 500,
        HttpRequestException or TimeoutException => true,
        _ => false
    };
}
