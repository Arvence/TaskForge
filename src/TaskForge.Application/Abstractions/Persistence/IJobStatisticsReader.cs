using TaskForge.Application.Jobs.Models;

namespace TaskForge.Application.Abstractions.Persistence;

public interface IJobStatisticsReader
{
    Task<JobStatistics> GetAsync(CancellationToken cancellationToken = default);
}
