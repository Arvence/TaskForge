using System.Text.Json;
using TaskForge.Application.Abstractions.Execution;

namespace TaskForge.Infrastructure.Jobs.Handlers;

public sealed class DelayJobHandler : IJobHandler
{
    private const int MaxDelayMilliseconds = 60_000;
    private static readonly JsonSerializerOptions SerializerOptions =
        new(JsonSerializerDefaults.Web);

    public string JobType => "delay";

    public async Task HandleAsync(
        string payloadJson,
        CancellationToken cancellationToken = default)
    {
        DelayJobPayload payload;

        try
        {
            payload = JsonSerializer.Deserialize<DelayJobPayload>(
                    payloadJson,
                    SerializerOptions)
                ?? throw new InvalidOperationException("A delay payload is required.");
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException(
                "The delay payload is invalid.",
                exception);
        }

        if (payload.DelayMilliseconds is < 1 or > MaxDelayMilliseconds)
        {
            throw new InvalidOperationException(
                $"Delay milliseconds must be between 1 and {MaxDelayMilliseconds}.");
        }

        await Task.Delay(payload.DelayMilliseconds, cancellationToken);
    }

    private sealed record DelayJobPayload(int DelayMilliseconds);
}
