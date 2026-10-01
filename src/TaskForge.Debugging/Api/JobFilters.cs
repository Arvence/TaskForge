namespace TaskForge.Debugging.Api;

internal sealed record JobFilters(string? Status, string? Type, string? Priority, string? ApplicationId = null)
{
    public static JobFilters Empty { get; } = new(null, null, null);
}
