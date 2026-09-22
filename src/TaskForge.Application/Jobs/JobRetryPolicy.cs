using Microsoft.Extensions.Options;

using TaskForge.Application.Workers;

namespace TaskForge.Application.Jobs;

public sealed class JobRetryPolicy(IOptions<WorkerOptions> options)
{
    private readonly WorkerOptions _options = options.Value;

    public TimeSpan GetDelay(int retryCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(retryCount);

        int exponent = Math.Min(retryCount, 30);
        long exponentialDelaySeconds = (long)_options.RetryDelaySeconds << exponent;
        long delaySeconds = Math.Min(exponentialDelaySeconds, _options.MaxRetryDelaySeconds);
        return TimeSpan.FromSeconds(delaySeconds);
    }
}
