using TaskForge.Domain.Jobs;

namespace TaskForge.UnitTests.Jobs;

public sealed class JobApplicationIdTests
{
    [Theory]
    [InlineData(" A-Project ", "a-project")]
    [InlineData("APP_2.Local", "app_2.local")]
    [InlineData("a", "a")]
    public void Job_stores_normalized_application_id(string applicationId, string expected)
    {
        Job job = new(Guid.NewGuid(), applicationId, "example", "{}", JobPriority.Normal, 3, 30, DateTimeOffset.UtcNow);

        Assert.Equal(expected, job.ApplicationId);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("a project")]
    [InlineData("a/project")]
    [InlineData("app\nkey")]
    [InlineData("\u00e4-project")]
    [InlineData("\u212a-project")]
    public void Job_rejects_invalid_application_id(string? applicationId)
    {
        Assert.Throws<ArgumentException>(() => new Job(Guid.NewGuid(), applicationId!, "example", "{}", JobPriority.Normal, 3, 30, DateTimeOffset.UtcNow));
    }

    [Fact]
    public void Application_id_length_limit_applies_after_trimming()
    {
        string maximum = new('A', 100);

        Assert.Equal(maximum.ToLowerInvariant(), JobApplicationId.Normalize($" {maximum} "));
        Assert.Throws<ArgumentException>(() => JobApplicationId.Normalize(maximum + "a"));
    }
}
