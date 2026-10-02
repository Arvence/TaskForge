using System.Text.Json;

using Microsoft.Extensions.DependencyInjection;

namespace TaskForge.SDK.Handlers;

internal abstract class JobHandlerAdapter
{
    protected static readonly JsonSerializerOptions PayloadOptions = new(JsonSerializerDefaults.Web)
    {
        RespectNullableAnnotations = true,
        RespectRequiredConstructorParameters = true
    };

    public abstract Type HandlerType { get; }
    public abstract Task<JsonElement?> ExecuteAsync(IServiceScopeFactory scopeFactory, JsonElement payload, CancellationToken cancellationToken);
}

internal sealed class JobHandlerAdapter<TPayload, THandler>(string jobType) : JobHandlerAdapter where TPayload : notnull where THandler : class, IJobHandler<TPayload>
{
    public override Type HandlerType => typeof(THandler);

    public override async Task<JsonElement?> ExecuteAsync(IServiceScopeFactory scopeFactory, JsonElement payload, CancellationToken cancellationToken)
    {
        TPayload model;
        try
        {
            if (payload.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            {
                throw new JsonException("A non-null job payload is required.");
            }

            model = payload.Deserialize<TPayload>(PayloadOptions) ?? throw new JsonException("A non-null job payload is required.");
        }
        catch (JsonException exception)
        {
            throw new InvalidJobPayloadException(jobType, typeof(TPayload), exception);
        }

        cancellationToken.ThrowIfCancellationRequested();
        await using AsyncServiceScope scope = scopeFactory.CreateAsyncScope();
        THandler handler = scope.ServiceProvider.GetRequiredService<THandler>();
        JsonElement? result = await handler.HandleAsync(model, cancellationToken).ConfigureAwait(false);
        try
        {
            return result?.Clone();
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException)
        {
            throw new JobResultSerializationException("The handler returned an unreadable JSON result.", exception);
        }
    }
}
