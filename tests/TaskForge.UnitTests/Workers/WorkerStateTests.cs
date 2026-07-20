using TaskForge.Domain.Workers;

namespace TaskForge.UnitTests.Workers;

public sealed class WorkerStateTests
{
    private static readonly DateTimeOffset StartedAt =
        new(2026, 7, 20, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void New_worker_starts_in_starting_state()
    {
        WorkerState worker = new("worker-01", StartedAt);

        Assert.Equal(WorkerStatus.Starting, worker.Status);
        Assert.Equal(StartedAt, worker.LastHeartbeatAtUtc);
        Assert.Null(worker.CurrentJobId);
    }

    [Fact]
    public void Idle_worker_can_take_and_release_job()
    {
        WorkerState worker = new("worker-01", StartedAt);
        Guid jobId = Guid.Parse("95d53689-1802-41e0-b25d-c64c62d460ba");

        worker.MarkIdle(StartedAt.AddSeconds(1));
        worker.AssignJob(jobId, StartedAt.AddSeconds(2));

        Assert.Equal(WorkerStatus.Busy, worker.Status);
        Assert.Equal(jobId, worker.CurrentJobId);

        worker.ReleaseJob(StartedAt.AddSeconds(3));

        Assert.Equal(WorkerStatus.Idle, worker.Status);
        Assert.Null(worker.CurrentJobId);
        Assert.Equal(3, worker.Version);
    }

    [Fact]
    public void Busy_worker_cannot_take_another_job()
    {
        WorkerState worker = new("worker-01", StartedAt);
        worker.MarkIdle(StartedAt.AddSeconds(1));
        worker.AssignJob(Guid.NewGuid(), StartedAt.AddSeconds(2));

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            () => worker.AssignJob(Guid.NewGuid(), StartedAt.AddSeconds(3)));

        Assert.Equal("Only an idle worker can take a job.", exception.Message);
    }

    [Fact]
    public void Offline_worker_clears_current_job()
    {
        WorkerState worker = new("worker-01", StartedAt);
        DateTimeOffset stoppedAt = StartedAt.AddMinutes(1);
        worker.MarkIdle(StartedAt.AddSeconds(1));
        worker.AssignJob(Guid.NewGuid(), StartedAt.AddSeconds(2));

        worker.MarkOffline(stoppedAt);

        Assert.Equal(WorkerStatus.Offline, worker.Status);
        Assert.Null(worker.CurrentJobId);
        Assert.Equal(stoppedAt, worker.StoppedAtUtc);
        Assert.Equal(stoppedAt, worker.LastHeartbeatAtUtc);
    }
}
