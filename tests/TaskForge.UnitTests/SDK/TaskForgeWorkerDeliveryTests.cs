using System.Net;
using System.Text;
using System.Text.Json;

using Microsoft.Extensions.DependencyInjection;

using TaskForge.SDK;
using TaskForge.SDK.Execution;

namespace TaskForge.UnitTests.SDK;

public sealed partial class TaskForgeWorkerTests
{
    [Theory]
    [InlineData(false, 0)]
    [InlineData(true, 0)]
    [InlineData(false, 408)]
    [InlineData(true, 429)]
    [InlineData(false, 503)]
    [InlineData(true, 500)]
    [InlineData(false, -1)]
    [InlineData(true, -2)]
    [InlineData(false, -3)]
    [InlineData(true, -3)]
    public async Task Transient_delivery_failure_repeats_the_exact_outcome_without_invoking_the_handler_again(bool failure, int response)
    {
        await using WorkerFixture fixture = new();
        if (failure)
        {
            fixture.State.Handle = (_, _) => throw new InvalidOperationException("The original business failure.");
        }

        int deliveries = 0;
        fixture.Server.SendReport = (_, _) =>
        {
            if (++deliveries > 1)
            {
                return Task.FromResult(DeliveryResponse());
            }

            return response switch
            {
                0 => throw new HttpRequestException("Connection lost."),
                -1 => throw new TimeoutException("Acknowledgement timed out."),
                -2 => throw new IOException("Response stream disconnected."),
                -3 => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new InterruptedResponseStream()) }),
                _ => Task.FromResult(DeliveryResponse((HttpStatusCode)response))
            };
        };
        Assignment assignment = Assignment.Create();
        fixture.Server.Assignments.Writer.TryWrite(assignment);
        await fixture.Worker.StartAsync(default);
        Report original = await ReadAsync(fixture.Server.Reports.Reader);
        Report repeated = await ReadAsync(fixture.Server.Reports.Reader);
        await ReadAsync(fixture.Server.Waits.Reader);
        await ReadAsync(fixture.Server.Waits.Reader);

        AssertReport(assignment, original, failure ? "fail" : "complete");
        Assert.Equal(original.Path, repeated.Path);
        Assert.Equal(original.RawBody, repeated.RawBody);
        Assert.Equal(2, fixture.Server.ReportCount);
        Assert.Equal(1, fixture.State.Created);
        Assert.Equal(1, fixture.State.Disposed);
        Assert.Equal(0, fixture.State.Active);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public async Task Exhausted_delivery_retries_release_the_slot_without_a_new_business_outcome(int retryCount)
    {
        await using WorkerFixture fixture = new(new() { ReportRetryCount = retryCount, TransportErrorDelay = TimeSpan.FromMilliseconds(1) });
        fixture.Server.SendReport = (_, _) => throw new HttpRequestException("Still disconnected.");
        fixture.Server.Assignments.Writer.TryWrite(Assignment.Create());
        await fixture.Worker.StartAsync(default);
        await ReadAsync(fixture.Server.Waits.Reader);
        await ReadAsync(fixture.Server.Waits.Reader);

        Report original = await ReadAsync(fixture.Server.Reports.Reader);
        Assert.EndsWith("/complete", original.Path);
        for (int retry = 0; retry < retryCount; retry++)
        {
            Report repeated = await ReadAsync(fixture.Server.Reports.Reader);
            Assert.Equal(original.Path, repeated.Path);
            Assert.Equal(original.RawBody, repeated.RawBody);
        }

        Assert.Equal(retryCount + 1, fixture.Server.ReportCount);
        Assert.Equal(1, fixture.State.Created);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest, "InvalidRequest")]
    [InlineData(HttpStatusCode.Unauthorized, "Unauthorized")]
    [InlineData(HttpStatusCode.NotFound, "Missing")]
    [InlineData(HttpStatusCode.Conflict, "Stale")]
    [InlineData(HttpStatusCode.Conflict, "AttemptTimedOut")]
    [InlineData(HttpStatusCode.Conflict, "Cancelled")]
    [InlineData(HttpStatusCode.Conflict, "ConflictingReport")]
    public async Task Definitive_rejection_stops_an_uncertain_report_without_converting_it_to_failure(HttpStatusCode status, string code)
    {
        await using WorkerFixture fixture = new();
        int deliveries = 0;
        fixture.Server.SendReport = (_, _) => ++deliveries == 1
            ? throw new HttpRequestException("Acknowledgement lost.")
            : Task.FromResult(DeliveryResponse(status, code));
        fixture.Server.Assignments.Writer.TryWrite(Assignment.Create());
        await fixture.Worker.StartAsync(default);

        if (status is HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized)
        {
            TaskForgeApiException error = await Assert.ThrowsAsync<TaskForgeApiException>(() => fixture.Worker.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Equal(status, error.StatusCode);
        }
        else
        {
            await ReadAsync(fixture.Server.Waits.Reader);
            await ReadAsync(fixture.Server.Waits.Reader);
        }

        Assert.Equal(2, fixture.Server.ReportCount);
        Report original = await ReadAsync(fixture.Server.Reports.Reader);
        Report repeated = await ReadAsync(fixture.Server.Reports.Reader);
        Assert.EndsWith("/complete", original.Path);
        Assert.Equal(original.Path, repeated.Path);
        Assert.Equal(original.RawBody, repeated.RawBody);
        Assert.Equal(1, fixture.State.Created);
    }

    [Fact]
    public async Task Deadline_during_transport_backoff_discards_the_pending_report_without_another_delivery()
    {
        ObservableClock clock = new();
        TimeSpan delay = TimeSpan.FromSeconds(5);
        await using WorkerFixture fixture = new(new() { TransportErrorDelay = delay }, clock);
        fixture.Server.SendReport = (_, _) => throw new HttpRequestException("Connection lost.");
        fixture.Server.Assignments.Writer.TryWrite(Assignment.Create() with { TimeoutSeconds = 2 });
        await fixture.Worker.StartAsync(default);
        await ReadAsync(fixture.Server.Reports.Reader);
        await WaitForDeliveryDelayAsync(clock, delay);
        Assert.Equal(1, fixture.Server.WaitCount);
        clock.Advance(TimeSpan.FromSeconds(2));
        await ReadAsync(fixture.Server.Waits.Reader);
        await ReadAsync(fixture.Server.Waits.Reader);
        clock.Advance(TimeSpan.FromMinutes(1));

        Assert.Equal(1, fixture.Server.ReportCount);
        Assert.Equal(1, fixture.State.Created);
        Assert.Equal(1, fixture.State.Disposed);
    }

    [Fact]
    public async Task Deadline_cancels_an_in_flight_report_retry_and_does_not_create_a_failure_report()
    {
        ObservableClock clock = new();
        TimeSpan delay = TimeSpan.FromSeconds(2);
        await using WorkerFixture fixture = new(new() { TransportErrorDelay = delay }, clock);
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource cancelled = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int deliveries = 0;
        fixture.Server.SendReport = async (_, token) =>
        {
            if (++deliveries == 1)
            {
                throw new HttpRequestException("Connection lost.");
            }

            entered.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                return DeliveryResponse();
            }
            finally
            {
                cancelled.TrySetResult();
            }
        };
        fixture.Server.Assignments.Writer.TryWrite(Assignment.Create() with { TimeoutSeconds = 10 });
        await fixture.Worker.StartAsync(default);
        await WaitForDeliveryDelayAsync(clock, delay);
        clock.Advance(delay);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, fixture.Server.WaitCount);
        clock.Advance(TimeSpan.FromSeconds(8));
        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await ReadAsync(fixture.Server.Waits.Reader);
        await ReadAsync(fixture.Server.Waits.Reader);

        Assert.Equal(2, fixture.Server.ReportCount);
        Assert.Equal(1, fixture.State.Created);
        Assert.All(new[] { await ReadAsync(fixture.Server.Reports.Reader), await ReadAsync(fixture.Server.Reports.Reader) }, report => Assert.EndsWith("/complete", report.Path));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Shutdown_cancels_pending_report_delivery_without_acquiring_more_work(bool inFlight)
    {
        ObservableClock clock = new();
        TimeSpan delay = TimeSpan.FromSeconds(2);
        await using WorkerFixture fixture = new(new() { TransportErrorDelay = delay }, clock);
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int deliveries = 0;
        fixture.Server.SendReport = async (_, token) =>
        {
            if (++deliveries == 1)
            {
                throw new HttpRequestException("Connection lost.");
            }

            entered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return DeliveryResponse();
        };
        fixture.Server.Assignments.Writer.TryWrite(Assignment.Create());
        await fixture.Worker.StartAsync(default);
        await WaitForDeliveryDelayAsync(clock, delay);
        if (inFlight)
        {
            clock.Advance(delay);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }

        await fixture.Worker.StopAsync(default).WaitAsync(TimeSpan.FromSeconds(5));
        await ObserveStoppedWorkerAsync(fixture);
        clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(inFlight ? 2 : 1, fixture.Server.ReportCount);
        Assert.Equal(1, fixture.Server.WaitCount);
        Assert.Equal(1, fixture.State.Created);
        Assert.Equal(1, fixture.State.Disposed);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(101)]
    public void Report_retry_count_is_bounded_before_host_start(int count)
    {
        ArgumentOutOfRangeException error = Assert.Throws<ArgumentOutOfRangeException>(() => new ServiceCollection().AddTaskForgeWorker(new() { ReportRetryCount = count }));
        Assert.Equal("ReportRetryCount", error.ParamName);
    }

    private static HttpResponseMessage DeliveryResponse(HttpStatusCode status = HttpStatusCode.OK, string? code = null) => new(status)
    {
        Content = new StringContent(JsonSerializer.Serialize(new { retryCount = 999, code }), Encoding.UTF8, "application/json")
    };

    private static async Task WaitForDeliveryDelayAsync(ObservableClock clock, TimeSpan delay)
    {
        while (await ReadAsync(clock.Timers.Reader) != delay)
        {
        }
    }
}
