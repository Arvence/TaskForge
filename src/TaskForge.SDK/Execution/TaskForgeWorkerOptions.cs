namespace TaskForge.SDK.Execution;

public sealed record TaskForgeWorkerOptions
{
    public int SlotCount { get; init; } = 1;
    public int WaitSeconds { get; init; } = 20;
    public TimeSpan NoWorkDelay { get; init; } = TimeSpan.FromMilliseconds(250);
    public TimeSpan TransportErrorDelay { get; init; } = TimeSpan.FromSeconds(1);

    internal void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(SlotCount);
        ArgumentOutOfRangeException.ThrowIfNegative(WaitSeconds);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(WaitSeconds, 30);
        ValidateDelay(NoWorkDelay, nameof(NoWorkDelay));
        ValidateDelay(TransportErrorDelay, nameof(TransportErrorDelay));
    }

    private static void ValidateDelay(TimeSpan delay, string name)
    {
        if (delay <= TimeSpan.Zero || delay.TotalMilliseconds > int.MaxValue)
        {
            throw new ArgumentOutOfRangeException(name, "Delay must be positive and at most Int32.MaxValue milliseconds.");
        }
    }
}
