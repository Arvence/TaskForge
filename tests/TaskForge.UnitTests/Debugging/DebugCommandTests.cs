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
                "high"
            ]);

        Assert.Equal(DebugCommandKind.Jobs, command.Kind);
        Assert.Equal("Retrying", command.Filters!.Status);
        Assert.Equal("http-request", command.Filters.Type);
        Assert.Equal("High", command.Filters.Priority);
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
            new JobFilters("Queued", "send email", "Critical"),
            pageSize: 20);

        Assert.Equal(
            "api/jobs?page=1&pageSize=20&status=Queued&type=send%20email&priority=Critical",
            result);
    }
}
