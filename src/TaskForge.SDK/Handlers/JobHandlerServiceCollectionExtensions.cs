using System.Collections.Frozen;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace TaskForge.SDK.Handlers;

public static class JobHandlerServiceCollectionExtensions
{
    public static IServiceCollection AddTaskForgeHandlers(this IServiceCollection services, Action<JobHandlerRegistryBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);
        if (services.Any(descriptor => descriptor.ServiceType == typeof(JobHandlerRegistry)))
        {
            throw new InvalidOperationException("TaskForge handlers are already configured. Register all mappings in one AddTaskForgeHandlers call.");
        }

        JobHandlerRegistryBuilder builder = new();
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

        services.AddSingleton(provider => new JobHandlerRegistry(provider.GetRequiredService<IServiceScopeFactory>(), handlers));
        return services;
    }
}
