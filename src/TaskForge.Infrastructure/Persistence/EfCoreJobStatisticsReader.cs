using Microsoft.EntityFrameworkCore;

using TaskForge.Application.Abstractions.Persistence;
using TaskForge.Application.Jobs.Models;
using TaskForge.Domain.Jobs;

namespace TaskForge.Infrastructure.Persistence;

public sealed class EfCoreJobStatisticsReader(TaskForgeDbContext dbContext) : IJobStatisticsReader
{
    public async Task<JobStatistics> GetAsync(CancellationToken cancellationToken = default, string? applicationId = null)
    {
        IQueryable<Job> jobs = dbContext.Jobs.AsNoTracking();
        if (applicationId is not null)
        {
            jobs = jobs.Where(job => job.ApplicationId == applicationId);
        }

        Dictionary<JobStatus, long> counts = await jobs
            .GroupBy(job => job.Status)
            .Select(group => new { Status = group.Key, Count = group.LongCount() })
            .ToDictionaryAsync(group => group.Status, group => group.Count, cancellationToken);

        return new JobStatistics(new JobStatusCounts(
            Pending: counts.GetValueOrDefault(JobStatus.Pending),
            Queued: counts.GetValueOrDefault(JobStatus.Queued),
            Processing: counts.GetValueOrDefault(JobStatus.Processing),
            Retrying: counts.GetValueOrDefault(JobStatus.Retrying),
            Completed: counts.GetValueOrDefault(JobStatus.Completed),
            DeadLettered: counts.GetValueOrDefault(JobStatus.DeadLettered),
            Cancelled: counts.GetValueOrDefault(JobStatus.Cancelled)));
    }
}
