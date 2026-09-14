namespace TaskForge.Application.Jobs.Models;

public sealed record JobStatistics(JobStatusCounts CountsByStatus)
{
    public long TotalJobs => CountsByStatus.Pending + CountsByStatus.Queued + CountsByStatus.Processing
        + CountsByStatus.Retrying + CountsByStatus.Completed + CountsByStatus.DeadLettered + CountsByStatus.Cancelled;

    public decimal? SuccessRatePercent => CountsByStatus.Completed + CountsByStatus.DeadLettered == 0
        ? null
        : Math.Round(CountsByStatus.Completed * 100m / (CountsByStatus.Completed + CountsByStatus.DeadLettered), 2, MidpointRounding.AwayFromZero);
}
