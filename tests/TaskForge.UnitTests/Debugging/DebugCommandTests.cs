using TaskForge.Debugging.Api;
using TaskForge.Debugging.Commands;

namespace TaskForge.UnitTests.Debugging;

public sealed class DebugCommandTests
{
    [Fact]
    public void Parse_NoArguments_SelectsDashboard()
    {
        DebugCommand command = DebugCommand.Parse([]);

        Assert.Equal(DebugCommandKind.Dashboard, command.Kind);
    }

    [Fact]
    public void Parse_JobsReadsAndNormalizesFilters()
    {
        DebugCommand command = DebugCommand.Parse(
            [
                "jobs",
                "--status",
                "retrying",
                "--type",
                "http-request",
                "--priority",
                "high",
                "--application-id",
                "billing-api"
            ]);

        Assert.Equal(DebugCommandKind.Jobs, command.Kind);
        Assert.Equal("Retrying", command.Filters!.Status);
        Assert.Equal("http-request", command.Filters.Type);
        Assert.Equal("High", command.Filters.Priority);
        Assert.Equal("billing-api", command.Filters.ApplicationId);
    }

    [Fact]
    public void Parse_JobsRejectsUnknownStatus()
    {
        ArgumentException exception = Assert.Throws<ArgumentException>(
            () => DebugCommand.Parse(["jobs", "--status", "Broken"]));

        Assert.Contains("Unknown status", exception.Message);
    }

    [Fact]
    public void BuildJobsPath_EscapesAndIncludesFilters()
    {
        string result = TaskForgeDebugClient.BuildJobsPath(
            new JobFilters("Queued", "send email", "Critical", "billing-api"),
            pageSize: 20);

        Assert.Equal(
            "api/jobs?page=1&pageSize=20&status=Queued&type=send%20email&priority=Critical&applicationId=billing-api",
            result);
    }

    [Theory]
    [InlineData(new[] { "jobs", "--application-id" }, "requires a value")]
    [InlineData(new[] { "jobs", "--application-id", " " }, "cannot be blank")]
    [InlineData(new[] { "jobs", "--application-id", "billing", "--application-id", "reporting" }, "only be provided once")]
    public void Parse_JobsRejectsInvalidApplicationOptions(string[] arguments, string expectedMessage)
    {
        ArgumentException exception = Assert.Throws<ArgumentException>(() => DebugCommand.Parse(arguments));

        Assert.Contains(expectedMessage, exception.Message);
    }
}
