using TaskForge.Application.Jobs.Models;

namespace TaskForge.Api.Jobs;

public sealed record JobPageResponse(
    IReadOnlyList<JobResponse> Items,
    int Page,
    int PageSize,
    int TotalCount,
    int TotalPages)
{
    public static JobPageResponse From(JobPage result) => new(
        result.Items.Select(JobResponse.From).ToArray(),
        result.Page,
        result.PageSize,
        result.TotalCount,
        result.TotalPages);
}