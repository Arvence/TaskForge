using TaskForge.Infrastructure.Jobs.Handlers;

namespace TaskForge.UnitTests.Infrastructure.Jobs.Handlers;

public sealed class DelayJobHandlerTests
{
    [Fact]
    public async Task Valid_payload_completes()
    {
        DelayJobHandler handler = new();

        await handler.HandleAsync("""{"delayMilliseconds":1}""");
    }

    [Theory]
    [InlineData("""{"delayMilliseconds":0}""")]
    [InlineData("""{"delayMilliseconds":60001}""")]
    [InlineData("""{""")]
    public async Task Invalid_payload_is_rejected(string payloadJson)
    {
        DelayJobHandler handler = new();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => handler.HandleAsync(payloadJson));
    }

    [Fact]
    public async Task Cancellation_is_propagated()
    {
        DelayJobHandler handler = new();
        using CancellationTokenSource cancellation = new();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => handler.HandleAsync(
                """{"delayMilliseconds":1000}""",
                cancellation.Token));
    }
}
