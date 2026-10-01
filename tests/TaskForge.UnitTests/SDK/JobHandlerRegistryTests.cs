using System.Collections.Concurrent;
using System.Text.Json;

using Microsoft.Extensions.DependencyInjection;

using TaskForge.SDK.Handlers;
using TaskForge.SDK.Jobs;

namespace TaskForge.UnitTests.SDK;

public sealed class JobHandlerRegistryTests
{
    [Fact]
    public async Task Registry_is_singleton_but_repeated_executions_get_fresh_scoped_handlers_and_dependencies()
    {
        ServiceCollection services = Services();
        services.AddTaskForgeHandlers(handlers => handlers.Register<MessagePayload, ProbeHandler>("send-email"));
        await using ServiceProvider provider = Build(services);
        JobHandlerRegistry registry = provider.GetRequiredService<JobHandlerRegistry>();
        ProbeState state = provider.GetRequiredService<ProbeState>();
        Assert.Empty(state.Handlers);
        Assert.Empty(state.Dependencies);
        await using (AsyncServiceScope scope = provider.CreateAsyncScope())
        {
            Assert.Same(registry, scope.ServiceProvider.GetRequiredService<JobHandlerRegistry>());
        }

        for (int i = 0; i < 3; i++)
        {
            JsonElement? result = await registry.ExecuteAsync("send-email", Payload());
            Assert.True(result!.Value.GetProperty("acknowledged").GetBoolean());
            Assert.Equal(i + 1, state.Handlers.Count);
            Assert.All(state.Handlers, handler => Assert.True(handler.Disposed));
            Assert.All(state.Dependencies, dependency => Assert.True(dependency.Disposed));
        }

        Assert.Equal(3, state.Handlers.Distinct().Count());
        Assert.Equal(3, state.Dependencies.Distinct().Count());
        Assert.All(state.Handlers, handler => Assert.Equal(new MessagePayload("user@example.com", 2), handler.Payload));
        Assert.Same(registry, provider.GetRequiredService<JobHandlerRegistry>());
    }

    [Theory]
    [InlineData("send-email")]
    [InlineData("SEND-EMAIL")]
    [InlineData(" send-EMAIL ")]
    public async Task Type_matching_is_case_insensitive_and_registration_names_are_trimmed(string lookup)
    {
        ServiceCollection services = Services();
        services.AddTaskForgeHandlers(handlers => handlers.Register<MessagePayload, ProbeHandler>(" send-email "));
        await using ServiceProvider provider = Build(services);
        JobHandlerRegistry registry = provider.GetRequiredService<JobHandlerRegistry>();

        await registry.ExecuteAsync(lookup, Payload());

        Assert.Equal(["send-email"], registry.RegisteredTypes);
        Assert.Single(provider.GetRequiredService<ProbeState>().Handlers);
    }

    [Fact]
    public async Task Different_mappings_deserialize_their_own_payload_types()
    {
        ServiceCollection services = Services();
        services.AddTaskForgeHandlers(handlers => handlers
            .Register<MessagePayload, ProbeHandler>("send-email")
            .Register<CountPayload, CountHandler>("count"));
        await using ServiceProvider provider = Build(services);
        JobHandlerRegistry registry = provider.GetRequiredService<JobHandlerRegistry>();
        JsonElement? result = await registry.ExecuteAsync("count", JsonSerializer.SerializeToElement(new { value = 7 }));
        Assert.Equal(14, result!.Value.GetInt32());
        Assert.Empty(provider.GetRequiredService<ProbeState>().Handlers);

        await registry.ExecuteAsync("send-email", JsonSerializer.SerializeToElement(new { RECIPIENT = "second@example.com", COPIES = 3 }));
        Assert.Equal(new MessagePayload("second@example.com", 3), Assert.Single(provider.GetRequiredService<ProbeState>().Handlers).Payload);
    }

    [Theory]
    [InlineData("send-email")]
    [InlineData("SEND-EMAIL")]
    [InlineData(" send-email ")]
    public void Duplicate_mappings_fail_during_startup_without_partially_registering_services(string duplicate)
    {
        ServiceCollection services = Services();
        int before = services.Count;

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() => services.AddTaskForgeHandlers(handlers => handlers
            .Register<MessagePayload, ProbeHandler>("send-email")
            .Register<CountPayload, CountHandler>(duplicate)));

        Assert.Contains("already has a handler mapping", error.Message);
        Assert.Contains(duplicate.Trim(), error.Message);
        Assert.Equal(before, services.Count);
    }

    [Fact]
    public void Retained_builder_and_second_configuration_call_cannot_change_frozen_mappings()
    {
        ServiceCollection services = Services();
        JobHandlerRegistryBuilder? captured = null;
        services.AddTaskForgeHandlers(handlers =>
        {
            captured = handlers;
            handlers.Register<MessagePayload, ProbeHandler>("send-email");
        });

        InvalidOperationException frozen = Assert.Throws<InvalidOperationException>(() => captured!.Register<CountPayload, CountHandler>("count"));
        Assert.Contains("frozen", frozen.Message);
        Assert.Throws<InvalidOperationException>(() => services.AddTaskForgeHandlers(handlers => handlers.Register<CountPayload, CountHandler>("count")));
        using ServiceProvider provider = Build(services);
        JobHandlerRegistry registry = provider.GetRequiredService<JobHandlerRegistry>();
        Assert.Equal(["send-email"], registry.RegisteredTypes);
        IList<string> names = Assert.IsAssignableFrom<IList<string>>(registry.RegisteredTypes);
        Assert.Throws<NotSupportedException>(() => names[0] = "changed");
    }

    [Fact]
    public void Registered_types_can_be_used_directly_in_wait_requests_and_aliases_share_only_the_mapping_type()
    {
        ServiceCollection services = Services();
        services.AddTaskForgeHandlers(handlers => handlers
            .Register<MessagePayload, ProbeHandler>("send-email")
            .Register<MessagePayload, ProbeHandler>("email-alias"));
        using ServiceProvider provider = Build(services);
        JobHandlerRegistry registry = provider.GetRequiredService<JobHandlerRegistry>();
        WaitForExecutionRequest request = new("worker", registry.RegisteredTypes);

        Assert.Equal(["email-alias", "send-email"], request.SupportedTypes);
        Assert.Single(services, descriptor => descriptor.ServiceType == typeof(ProbeHandler));
    }

    [Fact]
    public async Task Missing_registration_is_a_local_typed_error_without_activating_handlers()
    {
        ServiceCollection services = Services();
        services.AddTaskForgeHandlers(handlers => handlers.Register<MessagePayload, ProbeHandler>("send-email"));
        await using ServiceProvider provider = Build(services);
        JobHandlerNotFoundException error = await Assert.ThrowsAsync<JobHandlerNotFoundException>(() => provider
            .GetRequiredService<JobHandlerRegistry>().ExecuteAsync("unknown", Payload()));

        Assert.Equal("unknown", error.JobType);
        Assert.Contains("unknown", error.Message);
        Assert.Empty(provider.GetRequiredService<ProbeState>().Handlers);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("42")]
    [InlineData("{}")]
    [InlineData("{\"recipient\":null,\"copies\":1}")]
    [InlineData("{\"recipient\":\"user@example.com\",\"copies\":{}}")]
    [InlineData("{\"recipient\":\"user@example.com\"}")]
    public async Task Invalid_payloads_are_rejected_before_a_handler_or_scope_dependency_is_created(string json)
    {
        ServiceCollection services = Services();
        services.AddTaskForgeHandlers(handlers => handlers.Register<MessagePayload, ProbeHandler>("send-email"));
        await using ServiceProvider provider = Build(services);
        using JsonDocument document = JsonDocument.Parse(json);

        InvalidJobPayloadException error = await Assert.ThrowsAsync<InvalidJobPayloadException>(() => provider
            .GetRequiredService<JobHandlerRegistry>().ExecuteAsync("send-email", document.RootElement));

        Assert.Equal("send-email", error.JobType);
        Assert.Equal(typeof(MessagePayload), error.PayloadType);
        Assert.IsType<JsonException>(error.InnerException);
        Assert.Empty(provider.GetRequiredService<ProbeState>().Handlers);
        Assert.Empty(provider.GetRequiredService<ProbeState>().Dependencies);
    }

    [Fact]
    public async Task Undefined_payload_is_reported_as_invalid_payload()
    {
        ServiceCollection services = Services();
        services.AddTaskForgeHandlers(handlers => handlers.Register<MessagePayload, ProbeHandler>("send-email"));
        await using ServiceProvider provider = Build(services);
        await Assert.ThrowsAsync<InvalidJobPayloadException>(() => provider.GetRequiredService<JobHandlerRegistry>().ExecuteAsync("send-email", default));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(-2)]
    public async Task Handler_exceptions_propagate_unchanged_and_dispose_the_scope(int copies)
    {
        ServiceCollection services = Services();
        services.AddTaskForgeHandlers(handlers => handlers.Register<MessagePayload, ProbeHandler>("send-email"));
        await using ServiceProvider provider = Build(services);
        ProbeState state = provider.GetRequiredService<ProbeState>();

        Exception? error = await Record.ExceptionAsync(() => provider.GetRequiredService<JobHandlerRegistry>().ExecuteAsync("send-email", Payload(copies)));

        Assert.Same(state.HandlerFailure, error);
        Assert.True(Assert.Single(state.Handlers).Disposed);
        Assert.True(Assert.Single(state.Dependencies).Disposed);
        await provider.GetRequiredService<JobHandlerRegistry>().ExecuteAsync("send-email", Payload());
        Assert.Equal(2, state.Handlers.Count);
    }

    [Fact]
    public async Task Cancellation_reaches_the_handler_and_async_scope_disposal_finishes_before_return()
    {
        ServiceCollection services = Services();
        services.AddTaskForgeHandlers(handlers => handlers.Register<MessagePayload, ProbeHandler>("send-email"));
        await using ServiceProvider provider = Build(services);
        ProbeState state = provider.GetRequiredService<ProbeState>();
        using CancellationTokenSource cancellation = new();
        Task<JsonElement?> execution = provider.GetRequiredService<JobHandlerRegistry>().ExecuteAsync("send-email", Payload(-3), cancellation.Token);
        await state.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => execution.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(cancellation.Token, Assert.Single(state.Handlers).Token);
        Assert.True(Assert.Single(state.Handlers).Disposed);
        Assert.True(Assert.Single(state.Dependencies).Disposed);
    }

    [Fact]
    public async Task Precancelled_execution_does_not_resolve_a_handler()
    {
        ServiceCollection services = Services();
        services.AddTaskForgeHandlers(handlers => handlers.Register<MessagePayload, ProbeHandler>("send-email"));
        await using ServiceProvider provider = Build(services);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => provider.GetRequiredService<JobHandlerRegistry>()
            .ExecuteAsync("send-email", Payload(), new CancellationToken(canceled: true)));
        Assert.Empty(provider.GetRequiredService<ProbeState>().Handlers);
    }

    [Fact]
    public async Task Failed_handler_activation_still_disposes_dependencies_created_in_its_scope()
    {
        ServiceCollection services = Services();
        services.AddTaskForgeHandlers(handlers => handlers.Register<MessagePayload, ProbeHandler>("send-email"));
        await using ServiceProvider provider = Build(services);
        ProbeState state = provider.GetRequiredService<ProbeState>();
        state.FailConstruction = true;

        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.GetRequiredService<JobHandlerRegistry>().ExecuteAsync("send-email", Payload()));

        Assert.True(Assert.Single(state.Dependencies).Disposed);
        Assert.Empty(state.Handlers);
    }

    [Fact]
    public async Task Concurrent_executions_share_the_frozen_registry_and_have_independent_instances()
    {
        ServiceCollection services = Services();
        services.AddTaskForgeHandlers(handlers => handlers.Register<MessagePayload, ProbeHandler>("send-email"));
        await using ServiceProvider provider = Build(services);
        JobHandlerRegistry registry = provider.GetRequiredService<JobHandlerRegistry>();
        await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => registry.ExecuteAsync("send-email", Payload())));
        ProbeState state = provider.GetRequiredService<ProbeState>();

        Assert.Equal(20, state.Handlers.Distinct().Count());
        Assert.Equal(20, state.Dependencies.Distinct().Count());
        Assert.All(state.Handlers, handler => Assert.True(handler.Disposed));
        Assert.All(state.Dependencies, dependency => Assert.True(dependency.Disposed));
    }

    [Theory]
    [InlineData(ServiceLifetime.Scoped)]
    [InlineData(ServiceLifetime.Transient)]
    public async Task Existing_client_factories_are_resolved_inside_the_execution_scope(ServiceLifetime lifetime)
    {
        ServiceCollection services = Services();
        int factoryCalls = 0;
        ((IServiceCollection)services).Add(new ServiceDescriptor(typeof(ProbeHandler), provider =>
        {
            factoryCalls++;
            return new ProbeHandler(provider.GetRequiredService<ProbeState>(), provider.GetRequiredService<ExecutionDependency>());
        }, lifetime));
        services.AddTaskForgeHandlers(handlers => handlers.Register<MessagePayload, ProbeHandler>("send-email"));
        await using ServiceProvider provider = Build(services);
        await provider.GetRequiredService<JobHandlerRegistry>().ExecuteAsync("send-email", Payload());
        await provider.GetRequiredService<JobHandlerRegistry>().ExecuteAsync("send-email", Payload());
        Assert.Equal(2, factoryCalls);
    }

    [Fact]
    public void Singleton_handlers_are_rejected_during_startup()
    {
        ServiceCollection services = Services();
        services.AddSingleton<ProbeHandler>();
        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() => services.AddTaskForgeHandlers(handlers => handlers.Register<MessagePayload, ProbeHandler>("send-email")));
        Assert.Contains("cannot be a singleton", error.Message);
        Assert.DoesNotContain(services, descriptor => descriptor.ServiceType == typeof(JobHandlerRegistry));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public void Empty_type_names_are_rejected(string? jobType)
    {
        Assert.ThrowsAny<ArgumentException>(() => Services().AddTaskForgeHandlers(handlers => handlers.Register<MessagePayload, ProbeHandler>(jobType!)));
    }

    [Fact]
    public void Type_names_must_fit_the_server_contract()
    {
        Assert.Throws<ArgumentException>(() => Services().AddTaskForgeHandlers(handlers => handlers.Register<MessagePayload, ProbeHandler>(new string('x', 101))));
    }

    private static JsonElement Payload(int copies = 2) => JsonSerializer.SerializeToElement(new { recipient = "user@example.com", copies });

    private static ServiceCollection Services()
    {
        ServiceCollection services = new();
        services.AddSingleton<ProbeState>();
        services.AddScoped<ExecutionDependency>();
        return services;
    }

    private static ServiceProvider Build(IServiceCollection services) => services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });

    public sealed record MessagePayload(string Recipient, int Copies);
    public readonly record struct CountPayload(int Value);

    public sealed class CountHandler : IJobHandler<CountPayload>
    {
        public Task<JsonElement?> HandleAsync(CountPayload payload, CancellationToken cancellationToken = default) => Task.FromResult<JsonElement?>(JsonSerializer.SerializeToElement(payload.Value * 2));
    }

    public sealed class ProbeState
    {
        public ConcurrentQueue<ProbeHandler> Handlers { get; } = new();
        public ConcurrentQueue<ExecutionDependency> Dependencies { get; } = new();
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Exception? HandlerFailure { get; set; }
        public bool FailConstruction { get; set; }
    }

    public sealed class ExecutionDependency : IAsyncDisposable
    {
        public ExecutionDependency(ProbeState state)
        {
            state.Dependencies.Enqueue(this);
        }

        public JsonDocument Document { get; } = JsonDocument.Parse("{\"acknowledged\":true}");
        public bool Disposed { get; private set; }

        public async ValueTask DisposeAsync()
        {
            await Task.Yield();
            Document.Dispose();
            Disposed = true;
        }
    }

    public sealed class ProbeHandler : IJobHandler<MessagePayload>, IDisposable
    {
        private readonly ProbeState _state;
        private readonly ExecutionDependency _dependency;

        public ProbeHandler(ProbeState state, ExecutionDependency dependency)
        {
            if (state.FailConstruction)
            {
                throw new InvalidOperationException("Activation failed.");
            }

            _state = state;
            _dependency = dependency;
            state.Handlers.Enqueue(this);
        }

        public MessagePayload? Payload { get; private set; }
        public CancellationToken Token { get; private set; }
        public bool Disposed { get; private set; }

        public async Task<JsonElement?> HandleAsync(MessagePayload payload, CancellationToken cancellationToken = default)
        {
            Payload = payload;
            Token = cancellationToken;
            _state.Entered.TrySetResult();
            if (payload.Copies is -1 or -2)
            {
                _state.HandlerFailure = payload.Copies == -1 ? new JsonException("Handler JSON failure.") : new InvalidOperationException("Handler failure.");
                throw _state.HandlerFailure;
            }

            if (payload.Copies == -3)
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }

            await Task.Yield();
            return _dependency.Document.RootElement;
        }

        public void Dispose() => Disposed = true;
    }
}
