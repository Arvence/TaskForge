using System.Text.Json;
using System.Threading.Channels;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

using TaskForge.Cli;
using TaskForge.SDK;
using TaskForge.SDK.Execution;
using TaskForge.SDK.Handlers;
using TaskForge.SDK.Jobs;

namespace TaskForge.IntegrationTests.Cli;

[Collection("SQL Server")]
public sealed class GeneralWorkerTests(SqlServerFixture fixture) : SqlServerTest(fixture)
{
    private const string ApplicationId = "test-app";
    private static readonly string[] SupportedTypes = ["custom", "general"];

    [Fact]
    public async Task One_cli_worker_routes_both_job_types_with_at_most_five_active_attempts()
    {
        await using WebApplicationFactory<global::Program> server = CreateServer();
        using HttpClient http = server.CreateClient();
        DispatchProbe probe = new();
        using IHost cli = CreateCli(http, probe);
        TaskForgeClient client = cli.Services.GetRequiredService<TaskForgeClient>();
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(30));
        List<(Guid Id, string Type, int ExpectedResult)> jobs = [];
        for (int value = 1; value <= 6; value++)
        {
            bool add = value % 2 != 0;
            string type = value <= 3 ? "general" : "custom";
            JobResponse job = await client.SubmitAsync(new(type, JsonSerializer.SerializeToElement(new
            {
                operation = add ? "add" : "subtract",
                left = value,
                right = 10
            })), cancellationToken: deadline.Token);
            jobs.Add((job.Id, type, add ? value + 10 : value - 10));
        }

        Assert.IsType<TaskForgeWorker>(Assert.Single(cli.Services.GetServices<IHostedService>()));
        Assert.Equal(SupportedTypes, cli.Services.GetRequiredService<JobHandlerRegistry>().RegisteredTypes);
        await cli.StartAsync(deadline.Token);
        try
        {
            List<JobExecutionContext> running = [];
            for (int slot = 0; slot < 5; slot++)
            {
                running.Add(await probe.Started.Reader.ReadAsync(deadline.Token));
            }

            Assert.Equal(5, running.Select(context => context.WorkerId).Distinct().Count());
            Assert.Equal(5, running.Select(context => context.JobId).Distinct().Count());
            Assert.Equal(5, running.Select(context => context.AttemptId).Distinct().Count());
            Assert.All(running, context => Assert.Equal(ApplicationId, context.ApplicationId));
            Assert.Equal(SupportedTypes, running.Select(context => context.JobType).Distinct().Order(StringComparer.Ordinal));
            Guid waiting = Assert.Single(jobs.Select(job => job.Id).Except(running.Select(context => context.JobId)));
            Assert.Equal(JobStatus.Queued, (await client.GetJobAsync(waiting, deadline.Token)).Status);
            Assert.Empty(await client.GetAttemptsAsync(waiting, deadline.Token));

            probe.Release.SetResult();
            foreach ((Guid jobId, string type, int expectedResult) in jobs)
            {
                JobResponse completed = await WaitForTerminalAsync(client, jobId, deadline.Token);
                Assert.Equal(JobStatus.Completed, completed.Status);
                Assert.Equal(type, completed.Type);
                Assert.Equal(expectedResult, completed.Result!.Value.GetInt32());
                Assert.Equal(0, completed.RetryCount);
                Assert.Equal(JobAttemptOutcome.Succeeded, Assert.Single(await client.GetAttemptsAsync(jobId, deadline.Token)).Outcome);
            }
        }
        finally
        {
            using CancellationTokenSource stop = new(TimeSpan.FromSeconds(5));
            await cli.StopAsync(stop.Token);
        }
    }

    [Fact]
    public async Task Unknown_or_invalid_operations_fail_permanently_without_invoking_a_handler()
    {
        await using WebApplicationFactory<global::Program> server = CreateServer();
        using HttpClient http = server.CreateClient();
        DispatchProbe probe = new();
        using IHost cli = CreateCli(http, probe);
        TaskForgeClient client = cli.Services.GetRequiredService<TaskForgeClient>();
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(30));
        (string Payload, string ErrorCode)[] invalidPayloads =
        [
            ("""{"operation":"unknown","left":7,"right":2}""", "nonretryablejobexception"),
            ("""{"operation":"ADD","left":7,"right":2}""", "nonretryablejobexception"),
            ("""{"left":7,"right":2}""", "nonretryablejobexception"),
            ("""{"operation":"add","left":7}""", "invalidpayload"),
            ("""{"operation":"add","left":7,"right":2,"unexpected":true}""", "invalidpayload")
        ];
        List<(Guid Id, string ErrorCode)> jobs = [];
        foreach (string type in SupportedTypes)
        {
            foreach ((string payload, string errorCode) in invalidPayloads)
            {
                JobResponse job = await client.SubmitAsync(new(type, JsonSerializer.Deserialize<JsonElement>(payload)), cancellationToken: deadline.Token);
                jobs.Add((job.Id, errorCode));
            }
        }

        await cli.StartAsync(deadline.Token);
        try
        {
            foreach ((Guid jobId, string expectedError) in jobs)
            {
                JobResponse failed = await WaitForTerminalAsync(client, jobId, deadline.Token);
                Assert.Equal(JobStatus.DeadLettered, failed.Status);
                Assert.Equal(0, failed.RetryCount);
                JobAttemptResponse attempt = Assert.Single(await client.GetAttemptsAsync(jobId, deadline.Token));
                Assert.Equal(JobAttemptOutcome.PermanentlyFailed, attempt.Outcome);
                Assert.Equal(expectedError, attempt.ErrorCode);
            }

            Assert.False(probe.Started.Reader.TryRead(out _));
        }
        finally
        {
            using CancellationTokenSource stop = new(TimeSpan.FromSeconds(5));
            await cli.StopAsync(stop.Token);
        }
    }

    private WebApplicationFactory<global::Program> CreateServer() => new LifecycleApplicationFactory().WithWebHostBuilder(builder => builder
        .UseSetting("ConnectionStrings:TaskForge", ConnectionString).UseSetting("Worker:PollIntervalMilliseconds", "50"));

    private static IHost CreateCli(HttpClient http, DispatchProbe probe)
    {
        HostApplicationBuilder builder = Host.CreateApplicationBuilder();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["TaskForge:BaseUrl"] = http.BaseAddress!.AbsoluteUri,
            ["TaskForge:ApplicationId"] = ApplicationId,
            ["TaskForge:ApiKey"] = LifecycleApplicationFactory.KeyFor(ApplicationId),
            ["TaskForge:Worker:SlotCount"] = "5"
        });
        builder.Services.AddSingleton(http);
        builder.Services.AddSingleton(probe);
        builder.Services.AddTaskForgeCliWorker(builder.Configuration, operations => operations
            .Register<Operands, AddHandler>("add")
            .Register<Operands, SubtractHandler>("subtract"), SupportedTypes);
        return builder.Build();
    }

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

    public sealed record Operands(int Left, int Right);

    public sealed class AddHandler(DispatchProbe probe, JobExecutionContext context) : IJobHandler<Operands>
    {
        public Task<JsonElement?> HandleAsync(Operands payload, CancellationToken cancellationToken = default) =>
            probe.CompleteAsync(context, payload.Left + payload.Right, cancellationToken);
    }

    public sealed class SubtractHandler(DispatchProbe probe, JobExecutionContext context) : IJobHandler<Operands>
    {
        public Task<JsonElement?> HandleAsync(Operands payload, CancellationToken cancellationToken = default) =>
            probe.CompleteAsync(context, payload.Left - payload.Right, cancellationToken);
    }

    public sealed class DispatchProbe
    {
        public Channel<JobExecutionContext> Started { get; } = Channel.CreateUnbounded<JobExecutionContext>();
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<JsonElement?> CompleteAsync(JobExecutionContext context, int result, CancellationToken cancellationToken)
        {
            await Started.Writer.WriteAsync(context, cancellationToken);
            await Release.Task.WaitAsync(cancellationToken);
            return JsonSerializer.SerializeToElement(result);
        }
    }
}
