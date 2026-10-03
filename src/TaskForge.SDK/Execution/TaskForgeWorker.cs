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
    private readonly TimeProvider _timeProvider;
    private readonly IHostApplicationLifetime? _lifetime;
    private int _stopRequested;

    public TaskForgeWorker(TaskForgeClient client, JobHandlerRegistry registry, TaskForgeWorkerOptions options, ILogger<TaskForgeWorker>? logger = null, TimeProvider? timeProvider = null, IHostApplicationLifetime? lifetime = null)
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
        _timeProvider = timeProvider ?? TimeProvider.System;
        _lifetime = lifetime;
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        Interlocked.Exchange(ref _stopRequested, 1);
        using CancellationTokenSource timeout = new(_options.ShutdownTimeout, _timeProvider);
        using CancellationTokenSource boundedStop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        CancellationToken stopToken = boundedStop.Token;
        Task stopping = Task.Run(() => base.StopAsync(stopToken), CancellationToken.None);
        await stopping.WaitAsync(stopToken).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        if (ExecuteTask is { IsCompleted: false })
        {
            _logger.LogWarning("TaskForge shutdown waiting ended with an execution still running. Its slot remains occupied until it exits; the server can recover unreported work.");
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        string sessionId = Guid.NewGuid().ToString("N");
        using CancellationTokenSource slots = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, _lifetime?.ApplicationStopping ?? CancellationToken.None);
        await Task.WhenAll(Enumerable.Range(1, _options.SlotCount).Select(slot => RunSlotAsync($"taskforge-{sessionId}-{slot}", slots))).ConfigureAwait(false);
    }

    private async Task RunSlotAsync(string workerId, CancellationTokenSource slots)
    {
        CancellationToken stoppingToken = slots.Token;
        try
        {
            while (!stoppingToken.IsCancellationRequested && Volatile.Read(ref _stopRequested) == 0)
            {
                try
                {
                    ExecutionAssignmentResponse? assignment = await _client.WaitAsync(new(workerId, _registry.RegisteredTypes, _options.WaitSeconds), stoppingToken).ConfigureAwait(false);
                    if (stoppingToken.IsCancellationRequested || Volatile.Read(ref _stopRequested) != 0)
                    {
                        return;
                    }

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
            || assignment.TimeoutSeconds <= 0 || assignment.DeadlineAtUtc <= assignment.StartedAtUtc
            || string.IsNullOrWhiteSpace(assignment.Type))
        {
            throw new InvalidOperationException("TaskForge returned an invalid assignment identity. No handler was invoked and no outcome was reported.");
        }
    }

    private async Task ExecuteAssignmentAsync(ExecutionAssignmentResponse assignment, CancellationToken stoppingToken)
    {
        TimeSpan remaining = assignment.DeadlineAtUtc - _timeProvider.GetUtcNow();
        TimeSpan duration = assignment.DeadlineAtUtc - assignment.StartedAtUtc;
        TimeSpan maximum = TimeSpan.FromSeconds(assignment.TimeoutSeconds);
        remaining = remaining < duration ? remaining : duration;
        remaining = remaining < maximum ? remaining : maximum;
        if (remaining <= TimeSpan.Zero)
        {
            return;
        }

        using CancellationTokenSource deadline = new(remaining, _timeProvider);
        using CancellationTokenSource observedStop = new();
        using CancellationTokenSource execution = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, deadline.Token, observedStop.Token);
        using CancellationTokenSource monitorStop = CancellationTokenSource.CreateLinkedTokenSource(execution.Token);
        Task monitor = MonitorExecutionAsync(assignment, observedStop, monitorStop.Token);
        JsonElement? result = null;
        FailExecutionRequest? failure = null;
        try
        {
            result = await _registry.ExecuteAsync(assignment, execution.Token).ConfigureAwait(false);
            ValidateResult(result);
        }
        catch (OperationCanceledException) when (execution.IsCancellationRequested)
        {
            return;
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
        finally
        {
            await monitorStop.CancelAsync().ConfigureAwait(false);
            await monitor.ConfigureAwait(false);
        }

        stoppingToken.ThrowIfCancellationRequested();
        if (Volatile.Read(ref _stopRequested) != 0)
        {
            return;
        }

        CompleteExecutionRequest completion = new(assignment.WorkerId, result);
        await ReportOutcomeAsync(assignment, token => failure is null
            ? _client.CompleteAsync(assignment.JobId, assignment.AttemptId, completion, token)
            : _client.FailAsync(assignment.JobId, assignment.AttemptId, failure, token), deadline.Token, stoppingToken).ConfigureAwait(false);
    }

    private async Task ReportOutcomeAsync(ExecutionAssignmentResponse assignment, Func<CancellationToken, Task<JobResponse>> report, CancellationToken deadlineToken, CancellationToken stoppingToken)
    {
        using CancellationTokenSource delivery = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, deadlineToken);
        try
        {
            for (int retry = 0; retry <= _options.ReportRetryCount; retry++)
            {
                if (Volatile.Read(ref _stopRequested) != 0)
                {
                    return;
                }

                CancellationToken reportToken = retry == 0 && deadlineToken.IsCancellationRequested ? stoppingToken : delivery.Token;
                reportToken.ThrowIfCancellationRequested();
                try
                {
                    await report(reportToken).ConfigureAwait(false);
                    return;
                }
                catch (Exception exception) when (IsTransportFailure(exception) || exception is IOException)
                {
                    if (retry == _options.ReportRetryCount || deadlineToken.IsCancellationRequested)
                    {
                        _logger.LogWarning(exception, "TaskForge report delivery ended without acknowledgement for job {JobId}, attempt {AttemptId}. Server recovery remains authoritative.", assignment.JobId, assignment.AttemptId);
                        return;
                    }

                    _logger.LogWarning(exception, "TaskForge report delivery failed for job {JobId}, attempt {AttemptId}. Retrying the same outcome after a transport delay ({Retry}/{Limit}).", assignment.JobId, assignment.AttemptId, retry + 1, _options.ReportRetryCount);
                    await Task.Delay(_options.TransportErrorDelay, _timeProvider, delivery.Token).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (deadlineToken.IsCancellationRequested && !stoppingToken.IsCancellationRequested)
        {
            _logger.LogWarning("TaskForge report delivery reached the deadline for job {JobId}, attempt {AttemptId}. Server recovery remains authoritative.", assignment.JobId, assignment.AttemptId);
        }
        catch (TaskForgeApiException exception) when (exception.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Conflict)
        {
            _logger.LogWarning(exception, "TaskForge rejected the report for job {JobId}, attempt {AttemptId}. Waiting for a new assignment.", assignment.JobId, assignment.AttemptId);
        }
    }

    private async Task MonitorExecutionAsync(ExecutionAssignmentResponse assignment, CancellationTokenSource observedStop, CancellationToken cancellationToken)
    {
        try
        {
            while (true)
            {
                await Task.Delay(_options.StatusPollInterval, _timeProvider, cancellationToken).ConfigureAwait(false);
                JobResponse job;
                try
                {
                    job = await _client.GetJobAsync(assignment.JobId, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception exception) when (IsTransportFailure(exception))
                {
                    _logger.LogWarning(exception, "TaskForge status read failed for job {JobId}. The original execution deadline remains in effect.", assignment.JobId);
                    continue;
                }

                if (job.Id != assignment.JobId || !string.Equals(job.ApplicationId, assignment.ApplicationId, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException("TaskForge returned a job status with a different job or application identity.");
                }

                if (job.CancellationRequested || job.Status != JobStatus.Processing
                    || !string.Equals(job.OwningWorkerId, assignment.WorkerId, StringComparison.Ordinal) || job.StartedAtUtc != assignment.StartedAtUtc)
                {
                    await observedStop.CancelAsync().ConfigureAwait(false);
                    return;
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch
        {
            await observedStop.CancelAsync().ConfigureAwait(false);
            throw;
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
