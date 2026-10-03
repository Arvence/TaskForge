using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Logging;

using TaskForge.SampleClient;
using TaskForge.SampleClient.Handlers;
using TaskForge.SDK;
using TaskForge.SDK.Jobs;

namespace TaskForge.IntegrationTests.SDK;

[Collection("SQL Server")]
public sealed class SampleClientProcessTests(SqlServerFixture fixture) : SqlServerTest(fixture)
{
    [Fact]
    public async Task Two_applications_and_two_instances_for_one_application_run_as_independent_processes()
    {
        await using WebApplicationFactory<global::Program> server = CreateServer();
        using HttpClient http = server.CreateClient();
        await using ClientProxy proxy = await ClientProxy.StartAsync(http);
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(45));
        ConcurrentDictionary<string, bool> workersA = new(StringComparer.Ordinal);
        TaskCompletionSource bothA = new(TaskCreationOptions.RunContinuationsAsynchronously);
        proxy.BeforeComplete = async (report, token) =>
        {
            if (report.ApplicationId == "a-project")
            {
                workersA.TryAdd(report.WorkerId, true);
                if (workersA.Count == 2)
                {
                    bothA.TrySetResult();
                }

                await bothA.Task.WaitAsync(token);
            }
        };
        await using SampleProcess firstA = new(proxy.Address, "a-project");
        await using SampleProcess secondA = new(proxy.Address, "a-project");
        await using SampleProcess clientB = new(proxy.Address, "b-project");
        await WaitUntilAsync(() => proxy.Workers.Count == 3, deadline.Token, firstA, secondA, clientB);
        TaskForgeClient a = Client(http, "a-project");
        TaskForgeClient b = Client(http, "b-project");
        JobResponse[] jobs =
        [
            await SampleJobs.SubmitReportAsync(a, new("A-one", [new("A", 10m)]), deadline.Token),
            await SampleJobs.SubmitReportAsync(a, new("A-two", [new("A", 20m)]), deadline.Token),
            await SampleJobs.SubmitReportAsync(b, new("B-one", [new("B", 30m)]), deadline.Token)
        ];
        List<JobAttemptResponse> attempts = [];
        foreach (JobResponse job in jobs)
        {
            JobResponse completed = await WaitForCompletedAsync(a, job.Id, deadline.Token, firstA, secondA, clientB);
            JobAttemptResponse attempt = Assert.Single(await a.GetAttemptsAsync(job.Id, deadline.Token));
            Assert.Equal(JobAttemptOutcome.Succeeded, attempt.Outcome);
            Assert.Equal(job.ApplicationId, proxy.Workers[attempt.WorkerId]);
            Assert.Equal(0, completed.RetryCount);
            Assert.Equal(job.Payload.GetProperty("title").GetString(), completed.Result!.Value.GetProperty("title").GetString());
            Assert.Equal(job.Payload.GetProperty("entries")[0].GetProperty("amount").GetDecimal(), completed.Result.Value.GetProperty("totalAmount").GetDecimal());
            attempts.Add(attempt);
        }

        Assert.NotEqual(attempts[0].WorkerId, attempts[1].WorkerId);
        Assert.Equal(3, attempts.Select(attempt => attempt.WorkerId).Distinct().Count());
        Assert.Equal(3, proxy.Reports.Count);
        Assert.Equal(2, proxy.Workers.Count(worker => worker.Value == "a-project"));
        Assert.Single(proxy.Workers, worker => worker.Value == "b-project");
        await firstA.StopAsync();
        await secondA.StopAsync();
        await clientB.StopAsync();
        Assert.Contains("application a-project", await firstA.Output);
        Assert.Contains("application a-project", await secondA.Output);
        Assert.Contains("application b-project", await clientB.Output);
    }

    [Fact]
    public async Task Terminating_client_after_computation_causes_timeout_redistribution_and_identical_report_reexecution()
    {
        await using WebApplicationFactory<global::Program> server = CreateServer();
        using HttpClient http = server.CreateClient();
        TaskForgeClient client = Client(http, "a-project");
        await using ClientProxy proxy = await ClientProxy.StartAsync(http);
        TaskCompletionSource<Completion> withheld = new(TaskCreationOptions.RunContinuationsAsynchronously);
        proxy.BeforeComplete = async (report, token) =>
        {
            if (withheld.TrySetResult(report))
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            }
        };
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(45));
        JobResponse job = await client.SubmitAsync(new("generate-report", JsonSerializer.SerializeToElement(SampleJobs.ExampleReport(), new JsonSerializerOptions(JsonSerializerDefaults.Web)), MaxRetries: 1, TimeoutSeconds: 4), cancellationToken: deadline.Token);
        await using SampleProcess original = new(proxy.Address, "a-project");
        await WaitUntilAsync(() => withheld.Task.IsCompleted, deadline.Token, original);
        Completion firstReport = await withheld.Task.WaitAsync(deadline.Token);
        JobResponse processing = await client.GetJobAsync(job.Id, deadline.Token);
        Assert.Equal(JobStatus.Processing, processing.Status);
        Assert.Equal(JobAttemptOutcome.Running, Assert.Single(await client.GetAttemptsAsync(job.Id, deadline.Token)).Outcome);
        Assert.Equal(190.25m, firstReport.Result.GetProperty("totalAmount").GetDecimal());
        await original.StopAsync();

        await using SampleProcess replacement = new(proxy.Address, "a-project");
        JobResponse completed = await WaitForCompletedAsync(client, job.Id, deadline.Token, replacement);
        IReadOnlyList<JobAttemptResponse> history = await client.GetAttemptsAsync(job.Id, deadline.Token);
        Completion[] reports = proxy.Reports.ToArray();
        Assert.Equal(2, reports.Length);
        Assert.Equal([JobAttemptOutcome.TimedOut, JobAttemptOutcome.Succeeded], history.Select(attempt => attempt.Outcome));
        Assert.Equal([1, 2], history.Select(attempt => attempt.AttemptNumber));
        Assert.Equal(1, completed.RetryCount);
        Assert.Equal(reports[0].Result.GetRawText(), reports[1].Result.GetRawText());
        Assert.Equal(reports[1].Result.GetRawText(), completed.Result!.Value.GetRawText());
        Assert.Equal(reports[0].JobId, reports[1].JobId);
        Assert.NotEqual(reports[0].AttemptId, reports[1].AttemptId);
        Assert.NotEqual(reports[0].WorkerId, reports[1].WorkerId);
        Assert.Equal(history.Select(attempt => attempt.AttemptId), reports.Select(report => report.AttemptId));
        Assert.Equal(history.Select(attempt => attempt.WorkerId), reports.Select(report => report.WorkerId));
        Assert.All(reports, report => Assert.Equal("a-project", report.ApplicationId));
        await replacement.StopAsync();
        Assert.Contains($"attempt 1 ({history[0].AttemptId})", await original.Output);
        Assert.Contains($"attempt 2 ({history[1].AttemptId})", await replacement.Output);
    }

    private WebApplicationFactory<global::Program> CreateServer() => new WebApplicationFactory<global::Program>().WithWebHostBuilder(builder => builder
        .UseSetting("ConnectionStrings:TaskForge", ConnectionString).UseSetting("Worker:PollIntervalMilliseconds", "50").UseSetting("Worker:RetryDelaySeconds", "1"));

    private static TaskForgeClient Client(HttpClient http, string applicationId) => new(http, new() { BaseUrl = http.BaseAddress!, ApplicationId = applicationId });

    private static async Task WaitUntilAsync(Func<bool> condition, CancellationToken cancellationToken, params SampleProcess[] processes)
    {
        while (!condition())
        {
            foreach (SampleProcess process in processes)
            {
                await process.ThrowIfExitedAsync();
            }

            await Task.Delay(20, cancellationToken);
        }
    }

    private static async Task<JobResponse> WaitForCompletedAsync(TaskForgeClient client, Guid jobId, CancellationToken cancellationToken, params SampleProcess[] processes)
    {
        while (true)
        {
            foreach (SampleProcess process in processes)
            {
                await process.ThrowIfExitedAsync();
            }

            JobResponse job = await client.GetJobAsync(jobId, cancellationToken);
            if (job.Status == JobStatus.Completed)
            {
                return job;
            }

            Assert.NotEqual(JobStatus.DeadLettered, job.Status);
            await Task.Delay(20, cancellationToken);
        }
    }

    private sealed record Completion(Guid JobId, Guid AttemptId, string ApplicationId, string WorkerId, JsonElement Result);

    private sealed class SampleProcess : IAsyncDisposable
    {
        private readonly Process _process;
        public Task<string> Output { get; }
        private readonly Task<string> _errors;

        public SampleProcess(string baseUrl, string applicationId)
        {
            string directory = Path.Combine(AppContext.BaseDirectory, "sample-client");
            string assembly = Path.Combine(directory, "TaskForge.SampleClient.dll");
            Assert.True(File.Exists(assembly), $"Sample client output is missing at {assembly}. Build the integration test project before running process tests.");
            ProcessStartInfo start = new(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = directory
            };
            start.ArgumentList.Add(assembly);
            start.ArgumentList.Add("run");
            start.Environment["TaskForge__BaseUrl"] = baseUrl;
            start.Environment["TaskForge__ApplicationId"] = applicationId;
            start.Environment["DOTNET_ENVIRONMENT"] = "Production";
            _process = Process.Start(start) ?? throw new InvalidOperationException("Could not start the sample client.");
            Output = _process.StandardOutput.ReadToEndAsync();
            _errors = _process.StandardError.ReadToEndAsync();
        }

        public async Task ThrowIfExitedAsync()
        {
            if (_process.HasExited)
            {
                await Task.WhenAll(Output, _errors).WaitAsync(TimeSpan.FromSeconds(5));
                Assert.Fail($"Sample client exited unexpectedly with code {_process.ExitCode}.{Environment.NewLine}Standard output:{Environment.NewLine}{await Output}{Environment.NewLine}Standard error:{Environment.NewLine}{await _errors}");
            }
        }

        public async Task StopAsync()
        {
            await StopProcessAsync();
            Assert.True(string.IsNullOrWhiteSpace(await _errors), await _errors);
        }

        private async Task StopProcessAsync()
        {
            try
            {
                if (!_process.HasExited)
                {
                    _process.Kill(entireProcessTree: true);
                }
            }
            catch (InvalidOperationException) when (_process.HasExited)
            {
            }

            await _process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            await Task.WhenAll(Output, _errors).WaitAsync(TimeSpan.FromSeconds(5));
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                await StopProcessAsync();
            }
            finally
            {
                _process.Dispose();
            }
        }
    }

    private sealed class ClientProxy(WebApplication app) : IAsyncDisposable
    {
        public string Address => app.Urls.Single();
        public ConcurrentDictionary<string, string> Workers { get; } = new(StringComparer.Ordinal);
        public ConcurrentQueue<Completion> Reports { get; } = new();
        public Func<Completion, CancellationToken, Task>? BeforeComplete { get; set; }

        public static async Task<ClientProxy> StartAsync(HttpClient backend)
        {
            WebApplicationBuilder builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Production" });
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Logging.ClearProviders();
            WebApplication app = builder.Build();
            ClientProxy proxy = new(app);
            app.Run(context => proxy.ForwardAsync(backend, context));
            await app.StartAsync();
            return proxy;
        }

        private async Task ForwardAsync(HttpClient backend, HttpContext context)
        {
            string path = context.Request.Path.Value!;
            using HttpRequestMessage request = new(new HttpMethod(context.Request.Method), path + context.Request.QueryString);
            if (context.Request.Method == "POST")
            {
                using StreamReader reader = new(context.Request.Body);
                string json = await reader.ReadToEndAsync(context.RequestAborted);
                request.Content = new StringContent(json, Encoding.UTF8, "application/json");
                using JsonDocument document = JsonDocument.Parse(json);
                JsonElement body = document.RootElement;
                if (path == "/api/executions/wait")
                {
                    Workers[body.GetProperty("workerId").GetString()!] = body.GetProperty("applicationId").GetString()!;
                }
                else if (path.EndsWith("/complete", StringComparison.Ordinal))
                {
                    string[] segments = path.Split('/');
                    Completion report = new(Guid.Parse(segments[3]), Guid.Parse(segments[5]), body.GetProperty("applicationId").GetString()!,
                        body.GetProperty("workerId").GetString()!, body.GetProperty("result").Clone());
                    Reports.Enqueue(report);
                    if (BeforeComplete is not null)
                    {
                        await BeforeComplete(report, context.RequestAborted);
                    }
                }
            }

            using HttpResponseMessage response = await backend.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, context.RequestAborted);
            context.Response.StatusCode = (int)response.StatusCode;
            context.Response.ContentType = response.Content.Headers.ContentType?.ToString();
            await response.Content.CopyToAsync(context.Response.Body, context.RequestAborted);
        }

        public async ValueTask DisposeAsync()
        {
            using CancellationTokenSource stop = new(TimeSpan.FromSeconds(5));
            await app.StopAsync(stop.Token);
            await app.DisposeAsync();
        }
    }
}
