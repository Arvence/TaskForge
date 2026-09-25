using System.Data;
using System.Data.Common;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

using TaskForge.Api.Jobs;
using TaskForge.Application.Abstractions.Execution;
using TaskForge.Application.Jobs;
using TaskForge.Domain.Jobs;
using TaskForge.Infrastructure.Persistence;

namespace TaskForge.IntegrationTests.Api;

[Collection("SQL Server")]
public sealed class WaitForExecutionTests(SqlServerFixture fixture) : SqlServerTest(fixture)
{
    [Fact]
    public async Task Normal_startup_returns_one_committed_assignment_with_default_wait()
    {
        Job job = await SeedAsync();
        await SeedAsync();
        await using WebApplicationFactory<Program> factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ConnectionStrings:TaskForge", ConnectionString);
            builder.UseSetting("Worker:Count", "0");
        });
        using HttpClient client = factory.CreateClient();

        using HttpResponseMessage response = await client.PostAsJsonAsync("/api/executions/wait",
            new { applicationId = " TEST-APP ", workerId = "worker-01", supportedTypes = new[] { "REMOTE-ONLY" } });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl!.NoStore);
        ExecutionAssignmentResponse assignment = (await response.Content.ReadFromJsonAsync<ExecutionAssignmentResponse>())!;
        Assert.Equal(job.Id, assignment.JobId);
        Assert.Equal("test-app", assignment.ApplicationId);
        Assert.Equal("worker-01", assignment.WorkerId);
        Assert.Equal("remote-only", assignment.Type);
        Assert.Equal(1, assignment.Payload.GetProperty("value").GetInt32());
        Assert.Equal(300, assignment.TimeoutSeconds);
        Assert.Equal(1, assignment.AttemptNumber);
        Assert.Equal(assignment.StartedAtUtc.AddSeconds(300), assignment.DeadlineAtUtc);
        Assert.Equal(assignment.DeadlineAtUtc.AddSeconds(30), assignment.LeaseExpiresAtUtc);
        await using TaskForgeDbContext read = new(DatabaseOptions);
        Job persisted = await read.Jobs.SingleAsync(candidate => candidate.Id == assignment.JobId);
        JobAttempt attempt = await read.JobAttempts.SingleAsync();
        Assert.Equal(JobStatus.Processing, persisted.Status);
        Assert.Equal(assignment.WorkerId, persisted.OwningWorkerId);
        Assert.Equal(assignment.LeaseExpiresAtUtc, persisted.LeaseExpiresAtUtc);
        Assert.Equal(assignment.AttemptId, attempt.Id);
        Assert.Equal(JobAttemptOutcome.Running, attempt.Outcome);
        Assert.Equal(assignment.StartedAtUtc, attempt.StartedAtUtc);
        Assert.Equal(1, await read.Jobs.CountAsync(candidate => candidate.Status == JobStatus.Queued));
        using HttpResponseMessage openApi = await client.GetAsync("/openapi/v1.json");
        Assert.Contains("/api/executions/wait", await openApi.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Delayed_submission_uses_fresh_scopes_and_releases_connections_between_polls()
    {
        PollObserver observer = new();
        await using WebApplication app = await CreateAppAsync(observer);
        using HttpClient client = app.GetTestClient();
        Task<HttpResponseMessage> waiting = PostAsync(client, waitSeconds: 3);
        await observer.FirstDisposed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(0, observer.ActiveScopes);
        Job job = await SeedAsync();

        using HttpResponseMessage response = await waiting;

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(job.Id, (await response.Content.ReadFromJsonAsync<ExecutionAssignmentResponse>())!.JobId);
        Assert.True(observer.ContextIds.Count >= 2);
        Assert.Equal(observer.ContextIds.Count, observer.ContextIds.Distinct().Count());
        Assert.Equal(0, observer.ActiveScopes);
        Assert.All(observer.ReleasedConnections, released => Assert.True(released));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task Empty_wait_is_bounded_and_returns_204(int waitSeconds)
    {
        PollObserver observer = new();
        await using WebApplication app = await CreateAppAsync(observer);
        using HttpClient client = app.GetTestClient();
        Stopwatch elapsed = Stopwatch.StartNew();
        using HttpResponseMessage response = await PostAsync(client, waitSeconds);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Empty(await response.Content.ReadAsStringAsync());
        Assert.InRange(elapsed.Elapsed, TimeSpan.FromMilliseconds(waitSeconds * 900), TimeSpan.FromSeconds(waitSeconds + 5));
        if (waitSeconds == 0)
        {
            Assert.Single(observer.ContextIds);
        }

        Assert.Equal(0, observer.ActiveScopes);
    }

    [Fact]
    public async Task Wait_deadline_cancels_a_blocked_database_acquisition()
    {
        Job job = await SeedAsync();
        await using WebApplication app = await CreateAppAsync();
        using HttpClient client = app.GetTestClient();
        await using TaskForgeDbContext blocker = new(DatabaseOptions);
        await using var transaction = await blocker.Database.BeginTransactionAsync();
        await blocker.Database.ExecuteSqlInterpolatedAsync($"UPDATE [Jobs] SET [Version] = [Version] WHERE [Id] = {job.Id}");

        Stopwatch elapsed = Stopwatch.StartNew();
        using HttpResponseMessage response = await PostAsync(client, waitSeconds: 1).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(5));
        await transaction.RollbackAsync();
        Assert.Equal(JobStatus.Queued, (await blocker.Jobs.SingleAsync()).Status);
        Assert.Empty(await blocker.JobAttempts.ToListAsync());
    }

    [Fact]
    public async Task Future_retry_becomes_eligible_during_wait()
    {
        await using WebApplication app = await CreateAppAsync();
        using HttpClient client = app.GetTestClient();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        Job job = new(Guid.NewGuid(), "test-app", "remote-only", "{}", JobPriority.Normal, 3, 300, now.AddMinutes(-10));
        job.Queue(now.AddMinutes(-10));
        job.StartProcessing("old-worker", now.AddMinutes(-4), now.AddMinutes(-10));
        JobAttempt old = new(Guid.NewGuid(), job.Id, 1, "old-worker", now.AddMinutes(-10));
        old.Finish(JobAttemptOutcome.Failed, now.AddMinutes(-5));
        DateTimeOffset dueAt = now.AddMilliseconds(800);
        job.Fail("Retry later.", dueAt, now);
        await using (TaskForgeDbContext seed = new(DatabaseOptions))
        {
            seed.Jobs.Add(job);
            seed.JobAttempts.Add(old);
            await seed.SaveChangesAsync();
        }

        using HttpResponseMessage response = await PostAsync(client, waitSeconds: 3);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        ExecutionAssignmentResponse assignment = (await response.Content.ReadFromJsonAsync<ExecutionAssignmentResponse>())!;
        Assert.Equal(job.Id, assignment.JobId);
        Assert.Equal(2, assignment.AttemptNumber);
        Assert.True(assignment.StartedAtUtc >= dueAt);
        await using TaskForgeDbContext read = new(DatabaseOptions);
        Assert.Equal(1, (await read.Jobs.SingleAsync()).RetryCount);
        Assert.Equal(JobAttemptOutcome.Failed, (await read.JobAttempts.SingleAsync(attempt => attempt.Id == old.Id)).Outcome);
    }

    [Theory]
    [InlineData("other-app", "remote-only")]
    [InlineData("test-app", "unsupported")]
    public async Task Incompatible_work_is_not_claimed(string applicationId, string type)
    {
        await SeedAsync(applicationId, type);
        await using WebApplication app = await CreateAppAsync();
        using HttpClient client = app.GetTestClient();
        using HttpResponseMessage response = await PostAsync(client);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        await using TaskForgeDbContext read = new(DatabaseOptions);
        Assert.Equal(JobStatus.Queued, (await read.Jobs.SingleAsync()).Status);
        Assert.Empty(await read.JobAttempts.ToListAsync());
    }

    public static TheoryData<WaitForExecutionRequest, string> InvalidRequests => new()
    {
        { new(null, "worker", ["remote-only"], 0), "ApplicationId" },
        { new("bad app", "worker", ["remote-only"], 0), "ApplicationId" },
        { new(new string('a', 101), "worker", ["remote-only"], 0), "ApplicationId" },
        { new("test-app", null, ["remote-only"], 0), "WorkerId" },
        { new("test-app", "  ", ["remote-only"], 0), "WorkerId" },
        { new("test-app", new string('w', 201), ["remote-only"], 0), "WorkerId" },
        { new("test-app", "worker", null, 0), "SupportedTypes" },
        { new("test-app", "worker", [], 0), "SupportedTypes" },
        { new("test-app", "worker", [null], 0), "SupportedTypes" },
        { new("test-app", "worker", [" "], 0), "SupportedTypes" },
        { new("test-app", "worker", [new string('t', 101)], 0), "SupportedTypes" },
        { new("test-app", "worker", Enumerable.Repeat<string?>("remote-only", 101).ToArray(), 0), "SupportedTypes" },
        { new("test-app", "worker", ["remote-only"], -1), "WaitSeconds" },
        { new("test-app", "worker", ["remote-only"], 31), "WaitSeconds" }
    };

    [Theory]
    [MemberData(nameof(InvalidRequests))]
    public async Task Invalid_input_returns_400_before_opening_a_database_scope(WaitForExecutionRequest request, string field)
    {
        PollObserver observer = new();
        await using WebApplication app = await CreateAppAsync(observer);
        using HttpClient client = app.GetTestClient();
        using HttpResponseMessage response = await client.PostAsJsonAsync("/api/executions/wait", request);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        JsonElement body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(body.GetProperty("errors").TryGetProperty(field, out _));
        Assert.Empty(observer.ContextIds);
    }

    [Fact]
    public async Task Maximum_valid_bounds_are_accepted()
    {
        string applicationId = new('a', 100);
        string type = new('t', 100);
        await SeedAsync(applicationId, type);
        await using WebApplication app = await CreateAppAsync();
        using HttpClient client = app.GetTestClient();
        using HttpResponseMessage response = await client.PostAsJsonAsync("/api/executions/wait",
            new WaitForExecutionRequest(applicationId, new string('w', 200), Enumerable.Repeat<string?>(type, 100).ToArray(), 30));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Disconnect_before_acquisition_rolls_back_and_leaves_job_queued()
    {
        await SeedAsync();
        PauseBeforeSave pause = new();
        PollObserver observer = new();
        await using WebApplication app = await CreateAppAsync(observer, pause);
        using HttpClient client = app.GetTestClient();
        using CancellationTokenSource disconnected = new();
        Task<HttpResponseMessage> waiting = PostAsync(client, 30, cancellationToken: disconnected.Token);
        await pause.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        disconnected.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        await observer.FirstDisposed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await using TaskForgeDbContext read = new(DatabaseOptions);
        Assert.Equal(JobStatus.Queued, (await read.Jobs.SingleAsync()).Status);
        Assert.Empty(await read.JobAttempts.ToListAsync());
    }

    [Fact]
    public async Task Disconnect_after_commit_leaves_assignment_owned_without_claiming_more_work()
    {
        await SeedAsync();
        await SeedAsync();
        PauseAfterCommit pause = new();
        PollObserver observer = new();
        await using WebApplication app = await CreateAppAsync(observer, pause);
        using HttpClient client = app.GetTestClient();
        using CancellationTokenSource disconnected = new();
        Task<HttpResponseMessage> waiting = PostAsync(client, 30, cancellationToken: disconnected.Token);
        await pause.Committed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        disconnected.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        await observer.FirstDisposed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await using TaskForgeDbContext read = new(DatabaseOptions);
        Job claimed = await read.Jobs.SingleAsync(job => job.Status == JobStatus.Processing);
        Assert.Equal("worker-01", claimed.OwningWorkerId);
        Assert.NotNull(claimed.LeaseExpiresAtUtc);
        Assert.Equal(JobAttemptOutcome.Running, (await read.JobAttempts.SingleAsync()).Outcome);
        Assert.Equal(1, await read.Jobs.CountAsync(job => job.Status == JobStatus.Queued));
        Assert.Single(observer.ContextIds);
    }

    [Fact]
    public async Task Lost_commit_acknowledgement_is_not_replayed_by_the_database_execution_strategy()
    {
        await SeedAsync();
        await SeedAsync();
        ThrowAfterCommit failure = new();
        DbContextOptions<TaskForgeDbContext> options = new DbContextOptionsBuilder<TaskForgeDbContext>()
            .UseSqlServer(ConnectionString, sql => sql.EnableRetryOnFailure()).AddInterceptors(failure).Options;
        await using TaskForgeDbContext context = new(options);
        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(() => new EfCoreJobStore(context)
            .TryDistributeAsync("test-app", "worker", ["remote-only"], TimeSpan.FromSeconds(30), DateTimeOffset.UtcNow));
        Assert.Contains("commit could not be confirmed", error.Message);
        Assert.Equal(1, failure.Commits);
        await using TaskForgeDbContext read = new(DatabaseOptions);
        Assert.Equal(1, await read.Jobs.CountAsync(job => job.Status == JobStatus.Processing));
        Assert.Equal(1, await read.Jobs.CountAsync(job => job.Status == JobStatus.Queued));
        Assert.Single(await read.JobAttempts.ToListAsync());
    }

    [Fact]
    public async Task Host_shutdown_interrupts_wait_without_another_acquisition()
    {
        PollObserver observer = new();
        await using WebApplication app = await CreateAppAsync(observer);
        using HttpClient client = app.GetTestClient();
        Task<HttpResponseMessage> waiting = PostAsync(client, waitSeconds: 30);
        await observer.FirstDisposed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        app.Services.GetRequiredService<IHostApplicationLifetime>().StopApplication();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        Assert.Single(observer.ContextIds);
        Assert.Equal(0, observer.ActiveScopes);
    }

    [Fact]
    public async Task Competing_waiters_claim_a_job_only_once()
    {
        Job job = await SeedAsync();
        await using WebApplication app = await CreateAppAsync();
        using HttpClient client = app.GetTestClient();
        HttpResponseMessage[] responses = await Task.WhenAll(Enumerable.Range(0, 8).Select(index => PostAsync(client, workerId: $"worker-{index}")));
        try
        {
            HttpResponseMessage winner = Assert.Single(responses, response => response.StatusCode == HttpStatusCode.OK);
            Assert.Equal(7, responses.Count(response => response.StatusCode == HttpStatusCode.NoContent));
            Assert.Equal(job.Id, (await winner.Content.ReadFromJsonAsync<ExecutionAssignmentResponse>())!.JobId);
            await using TaskForgeDbContext read = new(DatabaseOptions);
            Assert.Equal(JobStatus.Processing, (await read.Jobs.SingleAsync()).Status);
            Assert.Single(await read.JobAttempts.ToListAsync());
        }
        finally
        {
            foreach (HttpResponseMessage response in responses)
            {
                response.Dispose();
            }
        }
    }

    private async Task<WebApplication> CreateAppAsync(PollObserver? observer = null, params IInterceptor[] interceptors)
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddTaskForgeSqlServer(ConnectionString);
        builder.Services.RemoveAll<DbContextOptions<TaskForgeDbContext>>();
        builder.Services.AddDbContext<TaskForgeDbContext>(options => options.UseSqlServer(ConnectionString, sql => sql.EnableRetryOnFailure()).AddInterceptors(interceptors));
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddSingleton<JobRetryPolicy>();
        builder.Services.AddSingleton<JobDistributionService>();
        builder.Services.AddSingleton(observer ?? new PollObserver());
        builder.Services.AddScoped<PollScope>();
        builder.Services.AddScoped<IJobQueue>(provider =>
        {
            provider.GetRequiredService<PollScope>();
            return provider.GetRequiredService<EfCoreJobStore>();
        });
        WebApplication app = builder.Build();
        app.MapTaskForgeExecutionWaitEndpoint();
        await app.StartAsync();
        return app;
    }

    private async Task<Job> SeedAsync(string applicationId = "test-app", string type = "remote-only")
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        Job job = new(Guid.NewGuid(), applicationId, type, "{\"value\":1}", JobPriority.Normal, 3, 300, now);
        job.Queue(now);
        await using TaskForgeDbContext context = new(DatabaseOptions);
        await new EfCoreJobStore(context).AddAsync(job);
        return job;
    }

    private static Task<HttpResponseMessage> PostAsync(HttpClient client, int waitSeconds = 0, string workerId = "worker-01", CancellationToken cancellationToken = default) =>
        client.PostAsJsonAsync("/api/executions/wait", new WaitForExecutionRequest("test-app", workerId, ["remote-only"], waitSeconds), cancellationToken);

    private sealed class PollObserver
    {
        public TaskCompletionSource FirstDisposed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public List<Guid> ContextIds { get; } = [];
        public List<bool> ReleasedConnections { get; } = [];
        public int ActiveScopes;
    }

    private sealed class PollScope : IDisposable
    {
        private readonly TaskForgeDbContext _context;
        private readonly PollObserver _observer;

        public PollScope(TaskForgeDbContext context, PollObserver observer)
        {
            _context = context;
            _observer = observer;
            lock (observer)
            {
                observer.ContextIds.Add(context.ContextId.InstanceId);
                observer.ActiveScopes++;
            }
        }

        public void Dispose()
        {
            lock (_observer)
            {
                _observer.ReleasedConnections.Add(_context.Database.CurrentTransaction is null && _context.Database.GetDbConnection().State == ConnectionState.Closed);
                _observer.ActiveScopes--;
                _observer.FirstDisposed.TrySetResult();
            }
        }
    }

    private sealed class PauseBeforeSave : SaveChangesInterceptor
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            Entered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return result;
        }
    }

    private sealed class PauseAfterCommit : DbTransactionInterceptor
    {
        public TaskCompletionSource Committed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData eventData, CancellationToken cancellationToken = default)
        {
            Committed.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }
    }

    private sealed class ThrowAfterCommit : DbTransactionInterceptor
    {
        public int Commits { get; private set; }

        public override Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData eventData, CancellationToken cancellationToken = default)
        {
            Commits++;
            throw new TimeoutException("The commit acknowledgement was lost.");
        }
    }
}
