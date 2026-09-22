using Microsoft.Extensions.Options;

using TaskForge.Application.Jobs;
using TaskForge.Application.Workers;

namespace TaskForge.UnitTests.Application.Jobs;

public sealed class JobRetryPolicyTests
{
    [Theory]
    [InlineData(0, 5)]
    [InlineData(1, 10)]
    [InlineData(2, 20)]
    [InlineData(3, 40)]
    [InlineData(4, 80)]
    [InlineData(5, 160)]
    [InlineData(6, 300)]
    [InlineData(7, 300)]
    [InlineData(30, 300)]
    [InlineData(int.MaxValue, 300)]
    public void Default_delay_uses_retry_count_before_failure_and_stays_capped(int retryCount, int expectedSeconds)
    {
        JobRetryPolicy policy = new(Options.Create(new WorkerOptions()));

        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), policy.GetDelay(retryCount));
    }

    [Theory]
    [InlineData(0, 3, 9, 3)]
    [InlineData(1, 3, 9, 6)]
    [InlineData(2, 3, 9, 9)]
    [InlineData(10, 3, 9, 9)]
    [InlineData(0, 7, 7, 7)]
    public void Delay_respects_configured_base_and_cap(int retryCount, int baseSeconds, int maxSeconds, int expectedSeconds)
    {
        JobRetryPolicy policy = new(Options.Create(new WorkerOptions
        {
            RetryDelaySeconds = baseSeconds,
            MaxRetryDelaySeconds = maxSeconds
        }));

        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), policy.GetDelay(retryCount));
    }

    [Fact]
    public void Negative_retry_count_is_rejected()
    {
        JobRetryPolicy policy = new(Options.Create(new WorkerOptions()));

        Assert.Throws<ArgumentOutOfRangeException>(() => policy.GetDelay(-1));
    }
}
