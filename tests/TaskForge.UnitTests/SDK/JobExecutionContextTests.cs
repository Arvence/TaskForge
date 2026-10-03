using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;

using Microsoft.Extensions.DependencyInjection;

using TaskForge.SDK.Execution;
using TaskForge.SDK.Handlers;
using TaskForge.SDK.Jobs;

namespace TaskForge.UnitTests.SDK;

public sealed class JobExecutionContextTests
{
    [Fact]
    public async Task Concurrent_assignments_get_isolated_metadata_available_to_handlers_and_scoped_dependencies()
    {
        ServiceCollection services = Services();
        await using ServiceProvider provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
        JobHandlerRegistry registry = provider.GetRequiredService<JobHandlerRegistry>();
        ProbeState state = provider.GetRequiredService<ProbeState>();
        ExecutionAssignmentResponse[] assignments = Enumerable.Range(1, 20).Select(Assignment).ToArray();
        Task<JsonElement?>[] executions = assignments.Select(assignment => registry.ExecuteAsync(assignment)).ToArray();
        try
        {
            for (int index = 0; index < assignments.Length; index++)
            {
                Observation observation = await state.Entered.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
                ExecutionAssignmentResponse assignment = Assert.Single(assignments, item => item.AttemptId == observation.Context.AttemptId);
                Assert.Equal(assignment.JobId, observation.Context.JobId);
                Assert.Equal(assignment.ApplicationId, observation.Context.ApplicationId);
                Assert.Equal(assignment.WorkerId, observation.Context.WorkerId);
                Assert.Equal(assignment.Type, observation.Context.JobType);
                Assert.Equal(assignment.AttemptNumber, observation.Context.AttemptNumber);
                Assert.Equal(assignment.StartedAtUtc, observation.Context.StartedAtUtc);
                Assert.Equal(assignment.DeadlineAtUtc, observation.Context.DeadlineAtUtc);
                Assert.NotEqual(observation.PayloadJobId, observation.Context.JobId);
                Assert.Same(observation.Context, observation.Dependency.Context);
                Assert.False(observation.Dependency.Disposed);
            }
        }
        finally
        {
            state.Release.TrySetResult();
        }

        JsonElement?[] results = await Task.WhenAll(executions);
        Assert.Equal(assignments.Select(assignment => assignment.JobId), results.Select(result => result!.Value.GetGuid()));
        Assert.Equal(20, state.Disposed.Count);
        Assert.Equal(20, state.Disposed.Select(context => context.AttemptId).Distinct().Count());
    }

    [Fact]
    public async Task Payload_only_invocation_does_not_invent_identity_for_a_context_dependent_handler()
    {
        await using ServiceProvider provider = Services().BuildServiceProvider();
        JobHandlerRegistry registry = provider.GetRequiredService<JobHandlerRegistry>();
        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(() => registry.ExecuteAsync("probe", JsonSerializer.SerializeToElement(new Payload(Guid.NewGuid()))));
        Assert.Contains("requires an execution assignment", error.Message);
        Assert.False(provider.GetRequiredService<ProbeState>().Entered.Reader.TryRead(out _));
    }

    [Fact]
    public async Task Cancellation_disposes_the_context_scope_and_the_next_attempt_receives_fresh_metadata()
    {
        await using ServiceProvider provider = Services().BuildServiceProvider();
        JobHandlerRegistry registry = provider.GetRequiredService<JobHandlerRegistry>();
        ProbeState state = provider.GetRequiredService<ProbeState>();
        ExecutionAssignmentResponse first = Assignment(1);
        using CancellationTokenSource cancellation = new();
        Task<JsonElement?> execution = registry.ExecuteAsync(first, cancellation.Token);
        Observation original = await state.Entered.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => execution);
        Assert.True(original.Dependency.Disposed);

        state.Release.TrySetResult();
        ExecutionAssignmentResponse second = first with { AttemptId = Guid.NewGuid(), AttemptNumber = 2, WorkerId = "replacement" };
        await registry.ExecuteAsync(second);
        Observation replacement = await state.Entered.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(original.Context.JobId, replacement.Context.JobId);
        Assert.NotEqual(original.Context.AttemptId, replacement.Context.AttemptId);
        Assert.Equal(2, replacement.Context.AttemptNumber);
        Assert.Equal("replacement", replacement.Context.WorkerId);
        Assert.NotSame(original.Context, replacement.Context);
        Assert.True(replacement.Dependency.Disposed);
    }

    private static ServiceCollection Services()
    {
        ServiceCollection services = new();
        services.AddSingleton<ProbeState>();
        services.AddScoped<ContextDependency>();
        services.AddTaskForgeHandlers(handlers => handlers.Register<Payload, ContextHandler>("probe"));
        return services;
    }

    private static ExecutionAssignmentResponse Assignment(int number)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        return new(Guid.NewGuid(), $"application-{number}", Guid.NewGuid(), number, $"worker-{number}", "probe",
            JsonSerializer.SerializeToElement(new Payload(Guid.NewGuid())), 30, now, now.AddSeconds(30), now.AddSeconds(60));
    }

    public sealed record Payload(Guid JobId);
    public sealed record Observation(JobExecutionContext Context, ContextDependency Dependency, Guid PayloadJobId);

    public sealed class ProbeState
    {
        public Channel<Observation> Entered { get; } = Channel.CreateUnbounded<Observation>();
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ConcurrentQueue<JobExecutionContext> Disposed { get; } = new();
    }

    public sealed class ContextDependency(JobExecutionContext context, ProbeState state) : IDisposable
    {
        public JobExecutionContext Context { get; } = context;
        public bool Disposed { get; private set; }

        public void Dispose()
        {
            Disposed = true;
            state.Disposed.Enqueue(Context);
        }
    }

    public sealed class ContextHandler(JobExecutionContext context, ContextDependency dependency, ProbeState state) : IJobHandler<Payload>
    {
        public async Task<JsonElement?> HandleAsync(Payload payload, CancellationToken cancellationToken = default)
        {
            state.Entered.Writer.TryWrite(new(context, dependency, payload.JobId));
            await state.Release.Task.WaitAsync(cancellationToken);
            return JsonSerializer.SerializeToElement(context.JobId);
        }
    }
}
