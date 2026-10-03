using System.Text.Json;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

using TaskForge.SampleClient;
using TaskForge.SampleClient.Handlers;
using TaskForge.SDK;
using TaskForge.SDK.Execution;
using TaskForge.SDK.Jobs;

namespace TaskForge.IntegrationTests.SDK;

[Collection("SQL Server")]
public sealed class SampleClientContractTests(SqlServerFixture fixture) : SqlServerTest(fixture)
{
    [Fact]
    public async Task Sample_report_persists_result_and_history_and_stopped_client_leaves_both_examples_queued()
    {
        await using WebApplicationFactory<global::Program> server = CreateServer();
        using HttpClient http = server.CreateClient();
        TaskForgeClient client = Client(http);
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(30));
        JobResponse submitted = await SampleJobs.SubmitReportAsync(client, SampleJobs.ExampleReport(), deadline.Token);
        await Task.Delay(250, deadline.Token);
        Assert.Equal(JobStatus.Queued, (await client.GetJobAsync(submitted.Id, deadline.Token)).Status);
        Assert.Empty(await client.GetAttemptsAsync(submitted.Id, deadline.Token));

        using IHost sample = CreateSample(client);
        await sample.StartAsync(deadline.Token);
        try
        {
            JobResponse completed = await WaitForTerminalAsync(client, submitted.Id, deadline.Token);
            Assert.Equal(JobStatus.Completed, completed.Status);
            Assert.Equal("a-project", completed.ApplicationId);
            Assert.Equal(190.25m, completed.Result!.Value.GetProperty("totalAmount").GetDecimal());
            Assert.Equal(3, completed.Result.Value.GetProperty("entryCount").GetInt32());
            Assert.InRange(completed.Result.Value.GetRawText().Length, 1, 4000);
            JobAttemptResponse attempt = Assert.Single(await client.GetAttemptsAsync(submitted.Id, deadline.Token));
            Assert.Equal(JobAttemptOutcome.Succeeded, attempt.Outcome);
            Assert.Equal(1, attempt.AttemptNumber);
            Assert.StartsWith("taskforge-", attempt.WorkerId);
            Assert.Equal(0, completed.RetryCount);
        }
        finally
        {
            await sample.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
        }

        JobResponse report = await SampleJobs.SubmitReportAsync(client, SampleJobs.ExampleReport(), deadline.Token);
        JobResponse request = await SampleJobs.SubmitHttpAsync(client, "http://localhost/example", deadline.Token);
        await Task.Delay(350, deadline.Token);
        foreach (Guid id in new[] { report.Id, request.Id })
        {
            Assert.Equal(JobStatus.Queued, (await client.GetJobAsync(id, deadline.Token)).Status);
            Assert.Empty(await client.GetAttemptsAsync(id, deadline.Token));
        }
    }

    [Fact]
    public async Task Exact_result_limit_persists_but_an_oversized_report_is_a_permanent_failure()
    {
        await using WebApplicationFactory<global::Program> server = CreateServer();
        using HttpClient http = server.CreateClient();
        TaskForgeClient client = Client(http);
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(30));
        ReportEntry?[] entries = Enumerable.Range(0, 40).Select(index => new ReportEntry($"C{index:D2}", 1m)).ToArray();
        GenerateReportPayload boundary = new("Boundary", entries);
        int remaining = 4000 - (await new GenerateReportJobHandler().HandleAsync(boundary, deadline.Token))!.Value.GetRawText().Length;
        for (int index = 0; remaining > 0 && index < entries.Length; index++)
        {
            int padding = Math.Min(remaining, 80 - entries[index]!.Category!.Length);
            entries[index] = entries[index]! with { Category = entries[index]!.Category + new string('X', padding) };
            remaining -= padding;
        }

        Assert.Equal(0, remaining);
        JobResponse accepted = await SampleJobs.SubmitReportAsync(client, boundary, deadline.Token);
        JobResponse oversized = await SampleJobs.SubmitReportAsync(client, boundary with { Title = "BoundaryX" }, deadline.Token);
        using IHost sample = CreateSample(client);
        await sample.StartAsync(deadline.Token);
        try
        {
            JobResponse completed = await WaitForTerminalAsync(client, accepted.Id, deadline.Token);
            Assert.Equal(JobStatus.Completed, completed.Status);
            Assert.Equal(4000, completed.Result!.Value.GetRawText().Length);
            Assert.Equal(40, completed.Result.Value.GetProperty("entryCount").GetInt32());
            Assert.Equal(40m, completed.Result.Value.GetProperty("totalAmount").GetDecimal());
            Assert.Equal(JobAttemptOutcome.Succeeded, Assert.Single(await client.GetAttemptsAsync(accepted.Id, deadline.Token)).Outcome);

            JobResponse rejected = await WaitForTerminalAsync(client, oversized.Id, deadline.Token);
            Assert.Equal(JobStatus.DeadLettered, rejected.Status);
            Assert.Null(rejected.Result);
            JobAttemptResponse attempt = Assert.Single(await client.GetAttemptsAsync(oversized.Id, deadline.Token));
            Assert.Equal(JobAttemptOutcome.PermanentlyFailed, attempt.Outcome);
            Assert.Equal("nonretryablejobexception", attempt.ErrorCode);
            Assert.Contains("4000", attempt.ErrorMessage);
        }
        finally
        {
            await sample.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    private WebApplicationFactory<global::Program> CreateServer() => new WebApplicationFactory<global::Program>().WithWebHostBuilder(builder => builder
        .UseSetting("ConnectionStrings:TaskForge", ConnectionString).UseSetting("Worker:PollIntervalMilliseconds", "50"));

    private static TaskForgeClient Client(HttpClient http) => new(http, new() { BaseUrl = http.BaseAddress!, ApplicationId = "a-project" });

    private static IHost CreateSample(TaskForgeClient client) => new HostBuilder().ConfigureServices(services =>
    {
        services.AddSingleton(client);
        services.AddSampleHandlers(new ConfigurationBuilder().Build());
        services.AddTaskForgeWorker(new() { WaitSeconds = 1 });
    }).Build();

    private static async Task<JobResponse> WaitForTerminalAsync(TaskForgeClient client, Guid jobId, CancellationToken cancellationToken)
    {
        while (true)
        {
            JobResponse job = await client.GetJobAsync(jobId, cancellationToken);
            if (job.Status is JobStatus.Completed or JobStatus.DeadLettered)
            {
                return job;
            }

            await Task.Delay(20, cancellationToken);
        }
    }
}
