using TaskForge.Domain.Jobs;

namespace TaskForge.UnitTests.Jobs;

public sealed class JobAttemptTests
{
    private static readonly DateTimeOffset StartedAt = new(2026, 9, 17, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Finishing_records_elapsed_time_and_error_without_changing_identity()
    {
        Guid jobId = Guid.NewGuid();
        JobAttempt attempt = new(Guid.NewGuid(), jobId, 2, "worker-02", StartedAt);

        attempt.Finish(JobAttemptOutcome.Failed, StartedAt.AddMilliseconds(1250), "RequestFailure", "The request failed.");

        Assert.Equal(jobId, attempt.JobId);
        Assert.Equal(2, attempt.AttemptNumber);
        Assert.Equal("worker-02", attempt.WorkerId);
        Assert.Equal(StartedAt, attempt.StartedAtUtc);
        Assert.Equal(StartedAt.AddMilliseconds(1250), attempt.FinishedAtUtc);
        Assert.Equal(1250, attempt.DurationMilliseconds);
        Assert.Equal(JobAttemptOutcome.Failed, attempt.Outcome);
        Assert.Equal("RequestFailure", attempt.ErrorCode);
        Assert.Equal("The request failed.", attempt.ErrorMessage);
    }

    [Fact]
    public void Completion_clamps_clock_regression_and_bounds_error_fields()
    {
        JobAttempt attempt = new(Guid.NewGuid(), Guid.NewGuid(), 1, "worker-01", StartedAt);

        attempt.Finish(JobAttemptOutcome.PermanentlyFailed, StartedAt.AddSeconds(-1), new string('C', 101), new string('E', 4001));

        Assert.Equal(StartedAt, attempt.FinishedAtUtc);
        Assert.Equal(0, attempt.DurationMilliseconds);
        Assert.Equal(100, attempt.ErrorCode!.Length);
        Assert.Equal(4000, attempt.ErrorMessage!.Length);
    }

    [Theory]
    [InlineData(JobAttemptOutcome.Running)]
    [InlineData((JobAttemptOutcome)999)]
    public void Running_or_unknown_outcomes_cannot_finish_an_attempt(JobAttemptOutcome outcome)
    {
        JobAttempt attempt = new(Guid.NewGuid(), Guid.NewGuid(), 1, "worker-01", StartedAt);

        Assert.Throws<ArgumentOutOfRangeException>(() => attempt.Finish(outcome, StartedAt));
        Assert.Equal(JobAttemptOutcome.Running, attempt.Outcome);
        Assert.Null(attempt.FinishedAtUtc);
    }

    [Fact]
    public void Finished_attempt_cannot_be_overwritten()
    {
        JobAttempt attempt = new(Guid.NewGuid(), Guid.NewGuid(), 1, "worker-01", StartedAt);
        attempt.Finish(JobAttemptOutcome.Succeeded, StartedAt.AddSeconds(1));

        Assert.Throws<InvalidOperationException>(() => attempt.Finish(JobAttemptOutcome.Failed, StartedAt.AddSeconds(2)));
        Assert.Equal(JobAttemptOutcome.Succeeded, attempt.Outcome);
        Assert.Null(attempt.ErrorCode);
        Assert.Null(attempt.ErrorMessage);
    }
}
