using System.Net;
using System.Text.Json;
using System.Threading.Channels;

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Time.Testing;

using TaskForge.SDK.Execution;
using TaskForge.SDK.Jobs;

namespace TaskForge.UnitTests.SDK;

public sealed partial class TaskForgeWorkerTests
{
    [Theory]
    [InlineData(true, JobStatus.Processing)]
    [InlineData(false, JobStatus.Cancelled)]
    [InlineData(false, JobStatus.Retrying)]
    public async Task Observed_cancellation_or_loss_of_authority_cancels_and_disposes_the_execution(bool requested, JobStatus status)
    {
        ObservableClock clock = new();
        await using WorkerFixture fixture = new(timeProvider: clock);
        fixture.Server.ReadJob = (assignment, _) => Task.FromResult(ScriptedServer.Status(assignment, requested, status));
        RunningHandler handler = await StartLongExecutionAsync(fixture, Assignment.Create());

        await PollAsync(fixture, clock);
        await handler.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await ReadAsync(fixture.Server.Waits.Reader);

        Assert.Equal(1, fixture.State.Disposed);
        Assert.Equal(0, fixture.State.Active);
        Assert.Equal(0, fixture.Server.ReportCount);
        Assert.Equal(1, fixture.State.Created);
    }

    [Fact]
    public async Task Deadline_uses_only_remaining_time_and_never_the_lease_grace()
    {
        ObservableClock clock = new();
        await using WorkerFixture fixture = new(timeProvider: clock);
        DateTimeOffset now = clock.GetUtcNow();
        Assignment assignment = Assignment.Create() with
        {
            StartedAtUtc = now.AddSeconds(-8),
            DeadlineAtUtc = now.AddSeconds(2),
            LeaseExpiresAtUtc = now.AddMinutes(10),
            TimeoutSeconds = 10
        };
        RunningHandler handler = await StartLongExecutionAsync(fixture, assignment);
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.False(handler.Token.IsCancellationRequested);
        clock.Advance(TimeSpan.FromSeconds(1));
        await handler.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await ReadAsync(fixture.Server.Waits.Reader);

        Assert.Equal(1, fixture.State.Disposed);
        Assert.Equal(0, fixture.Server.ReportCount);
    }

    [Fact]
    public async Task An_assignment_already_past_its_deadline_is_not_invoked_or_reported_as_a_made_up_timeout()
    {
        ObservableClock clock = new();
        await using WorkerFixture fixture = new(timeProvider: clock);
        fixture.Server.Assignments.Writer.TryWrite(Assignment.Create() with
        {
            StartedAtUtc = clock.GetUtcNow().AddSeconds(-31),
            DeadlineAtUtc = clock.GetUtcNow().AddSeconds(-1)
        });
        await fixture.Worker.StartAsync(default);
        await ReadAsync(fixture.Server.Waits.Reader);
        await ReadAsync(fixture.Server.Waits.Reader);
        Assert.Equal(0, fixture.State.Created);
        Assert.Equal(0, fixture.Server.ReportCount);
        Assert.False(fixture.Server.StatusReads.Reader.TryRead(out _));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task An_uncooperative_handler_holds_its_slot_until_exit_and_its_late_result_uses_the_original_attempt(bool serverCancellation)
    {
        ObservableClock clock = new();
        await using WorkerFixture fixture = new(timeProvider: clock);
        fixture.Server.ReportStatuses.Enqueue(HttpStatusCode.Conflict);
        if (serverCancellation)
        {
            fixture.Server.ReadJob = (assignment, _) => Task.FromResult(ScriptedServer.Status(assignment, true, JobStatus.Cancelled));
        }

        Assignment assignment = Assignment.Create() with { TimeoutSeconds = 2 };
        RunningHandler handler = await StartLongExecutionAsync(fixture, assignment, ignoresCancellation: true);
        try
        {
            if (serverCancellation)
            {
                await PollAsync(fixture, clock);
            }
            else
            {
                clock.Advance(TimeSpan.FromSeconds(2));
            }

            await handler.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
            clock.Advance(TimeSpan.FromMinutes(1));
            Assert.Equal(1, fixture.Server.WaitCount);
            Assert.Equal(1, fixture.State.Active);
            Assert.Equal(0, fixture.State.Disposed);
            Assert.Equal(0, fixture.Server.ReportCount);
            Assert.False(fixture.Worker.ExecuteTask!.IsCompleted);
        }
        finally
        {
            handler.Release.TrySetResult();
        }

        AssertReport(assignment, await ReadAsync(fixture.Server.Reports.Reader), "complete");
        await ReadAsync(fixture.Server.Waits.Reader);
        Assert.Equal(1, fixture.State.Disposed);
        Assert.Equal(0, fixture.State.Active);
    }

    [Theory]
    [InlineData("http")]
    [InlineData("network")]
    [InlineData("timeout")]
    [InlineData("stream")]
    public async Task Polling_failures_do_not_cancel_early_or_extend_the_original_deadline(string failure)
    {
        ObservableClock clock = new();
        await using WorkerFixture fixture = new(timeProvider: clock);
        fixture.Server.ReadJob = (_, _) => failure switch
        {
            "network" => throw new HttpRequestException("Unavailable."),
            "timeout" => throw new TimeoutException("Timed out."),
            "stream" => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new InterruptedResponseStream()) }),
            _ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable))
        };
        RunningHandler handler = await StartLongExecutionAsync(fixture, Assignment.Create() with { TimeoutSeconds = 4 });
        await PollAsync(fixture, clock);
        Assert.False(handler.Token.IsCancellationRequested);
        await PollAsync(fixture, clock);
        Assert.False(handler.Token.IsCancellationRequested);
        clock.Advance(TimeSpan.FromSeconds(2));
        await handler.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await ReadAsync(fixture.Server.Waits.Reader);
        Assert.Equal(0, fixture.Server.ReportCount);
        Assert.Equal(1, fixture.State.Disposed);
    }

    [Fact]
    public async Task A_stalled_status_read_is_cancelled_at_the_fixed_deadline()
    {
        ObservableClock clock = new();
        await using WorkerFixture fixture = new(timeProvider: clock);
        TaskCompletionSource readCancelled = new(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Server.ReadJob = async (_, token) =>
        {
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                throw new InvalidOperationException("The read should be cancelled.");
            }
            finally
            {
                readCancelled.TrySetResult();
            }
        };
        RunningHandler handler = await StartLongExecutionAsync(fixture, Assignment.Create() with { TimeoutSeconds = 2 });
        await PollAsync(fixture, clock);
        clock.Advance(TimeSpan.FromSeconds(1));
        await handler.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await readCancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await ReadAsync(fixture.Server.Waits.Reader);
        Assert.Equal(0, fixture.Server.ReportCount);
    }

    [Theory]
    [InlineData(-3600)]
    [InlineData(3600)]
    public async Task Wall_clock_adjustments_do_not_reschedule_the_execution_timer(int clockAdjustmentSeconds)
    {
        ObservableClock clock = new();
        await using WorkerFixture fixture = new(timeProvider: clock);
        RunningHandler handler = await StartLongExecutionAsync(fixture, Assignment.Create() with { TimeoutSeconds = 2 });
        clock.UtcOffset = TimeSpan.FromSeconds(clockAdjustmentSeconds);
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.False(handler.Token.IsCancellationRequested);
        clock.Advance(TimeSpan.FromSeconds(1));
        await handler.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await ReadAsync(fixture.Server.Waits.Reader);
        Assert.Equal(0, fixture.Server.ReportCount);
    }

    [Fact]
    public async Task A_slow_client_clock_cannot_give_more_than_the_assignment_duration()
    {
        ObservableClock clock = new();
        DateTimeOffset serverNow = clock.GetUtcNow().ToOffset(TimeSpan.FromHours(3));
        clock.UtcOffset = TimeSpan.FromHours(-1);
        await using WorkerFixture fixture = new(timeProvider: clock);
        RunningHandler handler = await StartLongExecutionAsync(fixture, Assignment.Create() with
        {
            StartedAtUtc = serverNow,
            DeadlineAtUtc = serverNow.AddSeconds(2),
            TimeoutSeconds = 2
        });
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.False(handler.Token.IsCancellationRequested);
        clock.Advance(TimeSpan.FromSeconds(1));
        await handler.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await ReadAsync(fixture.Server.Waits.Reader);
    }

    [Fact]
    public async Task Shutdown_cancels_a_pending_wait_and_does_not_acquire_more_work()
    {
        await using WorkerFixture fixture = new();
        await fixture.Worker.StartAsync(default);
        await ReadAsync(fixture.Server.Waits.Reader);
        await fixture.Worker.StopAsync(default).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(fixture.Worker.ExecuteTask!.IsCompleted);
        Assert.Equal(1, fixture.Server.WaitCount);
        Assert.Equal(0, fixture.State.Created);
    }

    [Fact]
    public async Task Application_stopping_cancels_execution_before_the_host_calls_worker_StopAsync()
    {
        using TestLifetime lifetime = new();
        await using WorkerFixture fixture = new(lifetime: lifetime);
        RunningHandler handler = await StartLongExecutionAsync(fixture, Assignment.Create());
        lifetime.StopApplication();
        await handler.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await fixture.Worker.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, fixture.State.Disposed);
        Assert.Equal(1, fixture.Server.WaitCount);
        Assert.Equal(0, fixture.Server.ReportCount);
    }

    [Fact]
    public async Task Shutdown_waiting_is_bounded_but_the_ignored_execution_keeps_its_scope_and_slot()
    {
        ObservableClock clock = new();
        await using WorkerFixture fixture = new(new() { ShutdownTimeout = TimeSpan.FromSeconds(3) }, clock);
        RunningHandler handler = await StartLongExecutionAsync(fixture, Assignment.Create(), ignoresCancellation: true);
        Task shutdown = fixture.Worker.StopAsync(default);
        try
        {
            await handler.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(shutdown.IsCompleted);
            clock.Advance(TimeSpan.FromSeconds(3));
            await shutdown.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(fixture.Worker.ExecuteTask!.IsCompleted);
            Assert.Equal(1, fixture.State.Active);
            Assert.Equal(0, fixture.State.Disposed);
            Assert.Equal(1, fixture.Server.WaitCount);
        }
        finally
        {
            handler.Release.TrySetResult();
        }

        await ObserveStoppedWorkerAsync(fixture);
        Assert.Equal(1, fixture.State.Disposed);
        Assert.Equal(0, fixture.Server.ReportCount);
        Assert.Equal(1, fixture.Server.WaitCount);
    }

    [Fact]
    public async Task An_already_cancelled_shutdown_budget_still_signals_the_execution()
    {
        await using WorkerFixture fixture = new();
        RunningHandler handler = await StartLongExecutionAsync(fixture, Assignment.Create());
        await fixture.Worker.StopAsync(new CancellationToken(canceled: true)).WaitAsync(TimeSpan.FromSeconds(5));
        await handler.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await ObserveStoppedWorkerAsync(fixture);
        Assert.Equal(0, fixture.Server.ReportCount);
        Assert.Equal(1, fixture.State.Disposed);
    }

    [Fact]
    public async Task Normal_completion_cancels_the_status_read_and_disposes_the_deadline_timer()
    {
        ObservableClock clock = new();
        await using WorkerFixture fixture = new(timeProvider: clock);
        TaskCompletionSource readCancelled = new(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Server.ReadJob = async (_, token) =>
        {
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                throw new InvalidOperationException("The read should be cancelled.");
            }
            finally
            {
                readCancelled.TrySetResult();
            }
        };
        RunningHandler handler = await StartLongExecutionAsync(fixture, Assignment.Create());
        await PollAsync(fixture, clock);
        handler.Release.TrySetResult();
        await ReadAsync(fixture.Server.Reports.Reader);
        await readCancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await ReadAsync(fixture.Server.Waits.Reader);
        clock.Advance(TimeSpan.FromMinutes(1));
        Assert.False(handler.Token.IsCancellationRequested);
        Assert.Equal(1, fixture.State.Disposed);
        Assert.False(fixture.Server.StatusReads.Reader.TryRead(out _));
    }

    [Theory]
    [InlineData("StatusPollInterval")]
    [InlineData("ShutdownTimeout")]
    public void Cancellation_options_require_positive_bounded_durations(string property)
    {
        TaskForgeWorkerOptions options = property == "StatusPollInterval"
            ? new() { StatusPollInterval = TimeSpan.Zero }
            : new() { ShutdownTimeout = Timeout.InfiniteTimeSpan };
        ArgumentOutOfRangeException error = Assert.Throws<ArgumentOutOfRangeException>(() => new Microsoft.Extensions.DependencyInjection.ServiceCollection().AddTaskForgeWorker(options));
        Assert.Equal(property, error.ParamName);
    }

    private static async Task<RunningHandler> StartLongExecutionAsync(WorkerFixture fixture, Assignment assignment, bool ignoresCancellation = false)
    {
        RunningHandler handler = new();
        fixture.State.Handle = async (_, token) =>
        {
            handler.Token = token;
            using CancellationTokenRegistration registration = token.Register(() => handler.Cancelled.TrySetResult());
            handler.Entered.TrySetResult();
            try
            {
                if (ignoresCancellation)
                {
                    await handler.Release.Task;
                }
                else
                {
                    await handler.Release.Task.WaitAsync(token);
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                handler.Cancelled.TrySetResult();
                throw;
            }

            return JsonSerializer.SerializeToElement(42);
        };
        fixture.Server.Assignments.Writer.TryWrite(assignment);
        await fixture.Worker.StartAsync(default);
        await ReadAsync(fixture.Server.Waits.Reader);
        await handler.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        return handler;
    }

    private static async Task PollAsync(WorkerFixture fixture, ObservableClock clock)
    {
        TimeSpan due;
        do
        {
            due = await ReadAsync(clock.Timers.Reader);
        } while (due != TimeSpan.FromSeconds(1));
        clock.Advance(TimeSpan.FromSeconds(1));
        await ReadAsync(fixture.Server.StatusReads.Reader);
    }

    private static async Task ObserveStoppedWorkerAsync(WorkerFixture fixture)
    {
        Exception? failure = await Record.ExceptionAsync(() => fixture.Worker.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.True(failure is null or OperationCanceledException, failure?.ToString());
    }

    private sealed class RunningHandler
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public CancellationToken Token { get; set; }
    }

    private sealed class ObservableClock : TimeProvider
    {
        private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 10, 2, 0, 0, 0, TimeSpan.Zero));
        public Channel<TimeSpan> Timers { get; } = Channel.CreateUnbounded<TimeSpan>();
        public TimeSpan UtcOffset { get; set; }
        public override long TimestampFrequency => _clock.TimestampFrequency;
        public override DateTimeOffset GetUtcNow() => _clock.GetUtcNow() + UtcOffset;
        public override long GetTimestamp() => _clock.GetTimestamp();
        public void Advance(TimeSpan duration) => _clock.Advance(duration);

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            ITimer timer = _clock.CreateTimer(callback, state, dueTime, period);
            Timers.Writer.TryWrite(dueTime);
            return timer;
        }
    }

    private sealed class TestLifetime : IHostApplicationLifetime, IDisposable
    {
        private readonly CancellationTokenSource _stopping = new();
        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => _stopping.Token;
        public CancellationToken ApplicationStopped => CancellationToken.None;
        public void StopApplication() => _stopping.Cancel();
        public void Dispose() => _stopping.Dispose();
    }
}
