using System.Text.Json;

using TaskForge.Application.Jobs.Models;

namespace TaskForge.UnitTests.Application.Jobs;

public sealed class JobStatisticsTests
{
    [Fact]
    public void Example_counts_produce_the_expected_total_and_success_rate()
    {
        JobStatistics stats = new(new JobStatusCounts(Queued: 12, Processing: 3, Retrying: 5, Completed: 70, DeadLettered: 8, Cancelled: 2));

        Assert.Equal(100L, stats.TotalJobs);
        Assert.Equal(89.74m, stats.SuccessRatePercent);
    }

    [Fact]
    public void Empty_stats_serialize_all_seven_zero_counts_and_an_explicit_null_rate()
    {
        string json = JsonSerializer.Serialize(new JobStatistics(new JobStatusCounts()), new JsonSerializerOptions(JsonSerializerDefaults.Web));
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;

        Assert.Equal(3, root.EnumerateObject().Count());
        Assert.Equal(0L, root.GetProperty("totalJobs").GetInt64());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("successRatePercent").ValueKind);
        JsonElement counts = root.GetProperty("countsByStatus");
        Assert.Equal(7, counts.EnumerateObject().Count());
        foreach (string status in new[] { "pending", "queued", "processing", "retrying", "completed", "deadLettered", "cancelled" })
        {
            Assert.Equal(0L, counts.GetProperty(status).GetInt64());
        }
    }

    [Fact]
    public void Unfinished_and_cancelled_jobs_do_not_define_a_success_rate()
    {
        JobStatistics stats = new(new JobStatusCounts(Pending: 1, Queued: 2, Processing: 3, Retrying: 4, Cancelled: 5));

        Assert.Equal(15L, stats.TotalJobs);
        Assert.Null(stats.SuccessRatePercent);
    }

    [Theory]
    [InlineData(1, 0, 100)]
    [InlineData(0, 1, 0)]
    [InlineData(1, 2, 33.33)]
    [InlineData(1, 31, 3.13)]
    public void Success_rate_uses_only_completed_and_dead_lettered_jobs(long completed, long deadLettered, double expected)
    {
        JobStatistics stats = new(new JobStatusCounts(Pending: 10, Queued: 20, Processing: 30, Retrying: 40, Completed: completed, DeadLettered: deadLettered, Cancelled: 50));

        Assert.Equal((decimal)expected, stats.SuccessRatePercent);
    }

    [Fact]
    public void Counters_and_percentage_support_values_above_int_max_value()
    {
        long count = (long)int.MaxValue + 1;
        JobStatistics stats = new(new JobStatusCounts(count, count, count, count, count, count, count));

        Assert.Equal(7 * count, stats.TotalJobs);
        Assert.Equal(50m, stats.SuccessRatePercent);
        Assert.All(typeof(JobStatusCounts).GetProperties(), property => Assert.Equal(typeof(long), property.PropertyType));
        Assert.Equal(typeof(long), typeof(JobStatistics).GetProperty(nameof(JobStatistics.TotalJobs))!.PropertyType);
    }
}
