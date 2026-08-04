using TaskForge.Domain.Jobs;

namespace TaskForge.Application.Jobs.Models;

public sealed record JobPage(
    IReadOnlyList<Job> Items,
    int Page,
    int PageSize,
    int TotalCount)
{
    public int TotalPages => TotalCount == 0
        ? 0
        : (int)Math.Ceiling((double)TotalCount / PageSize);
}