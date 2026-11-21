using System.Collections.Frozen;
using System.Text.Json;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using TaskForge.SDK.Execution;

namespace TaskForge.SDK.Handlers;

public static class JobHandlerServiceCollectionExtensions
{
    public static IServiceCollection AddTaskForgeHandlers(this IServiceCollection services, Action<JobHandlerRegistryBuilder> configure)
        => AddHandlers(services, configure, null, null, null);

    public static IServiceCollection AddTaskForgeHandlers(this IServiceCollection services, Action<JobHandlerRegistryBuilder> configure, IReadOnlyCollection<string> supportedTypes, Func<JsonElement, (string Operation, JsonElement Parameters)> selectOperation, JsonSerializerOptions? parameterOptions = null)
    {
        ArgumentNullException.ThrowIfNull(supportedTypes);
        ArgumentNullException.ThrowIfNull(selectOperation);
        string[] advertisedTypes = supportedTypes.Select(JobHandlerRegistryBuilder.NormalizeJobType)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        return AddHandlers(services, configure, advertisedTypes, selectOperation, parameterOptions);
    }

    private static IServiceCollection AddHandlers(IServiceCollection services, Action<JobHandlerRegistryBuilder> configure, string[]? supportedTypes, Func<JsonElement, (string Operation, JsonElement Parameters)>? selectOperation, JsonSerializerOptions? parameterOptions)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);
        if (services.Any(descriptor => descriptor.ServiceType == typeof(JobHandlerRegistry)))
        {
            throw new InvalidOperationException("TaskForge handlers are already configured. Register all mappings in one AddTaskForgeHandlers call.");
        }

        JobHandlerRegistryBuilder builder = new(selectOperation is not null);
        configure(builder);
        FrozenDictionary<string, JobHandlerAdapter> handlers = builder.Freeze();
        Type[] handlerTypes = handlers.Values.Select(handler => handler.HandlerType).Distinct().ToArray();
        foreach (Type handlerType in handlerTypes)
        {
            if (services.Any(descriptor => descriptor.ServiceType == handlerType && descriptor.Lifetime == ServiceLifetime.Singleton))
            {
                throw new InvalidOperationException($"Handler '{handlerType}' cannot be a singleton. Register it as scoped or transient for per-execution resolution.");
            }
        }

        foreach (Type handlerType in handlerTypes)
        {
            services.TryAdd(ServiceDescriptor.Scoped(handlerType, handlerType));
        }

        services.AddScoped<JobExecutionScope>();
        services.AddScoped<JobExecutionContext>(provider => provider.GetRequiredService<JobExecutionScope>().Context
            ?? throw new InvalidOperationException("JobExecutionContext requires an execution assignment. Invoke the registry with ExecuteAsync(assignment, cancellationToken)."));
        services.AddSingleton(provider => new JobHandlerRegistry(provider.GetRequiredService<IServiceScopeFactory>(), handlers, supportedTypes, selectOperation, parameterOptions));
        return services;
    }
}
