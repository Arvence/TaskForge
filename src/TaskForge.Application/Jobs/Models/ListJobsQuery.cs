using TaskForge.Domain.Jobs;

namespace TaskForge.Application.Jobs.Models;

public sealed record ListJobsQuery(
    JobStatus? Status = null,
    string? Type = null,
    JobPriority? Priority = null,
    int Page = 1,
    int PageSize = 50)
{
    public const int DefaultPageSize = 50;
    public const int MaximumPageSize = 100;
}