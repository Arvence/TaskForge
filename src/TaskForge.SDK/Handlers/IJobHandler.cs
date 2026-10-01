using System.Text.Json;

namespace TaskForge.SDK.Handlers;

public interface IJobHandler<in TPayload> where TPayload : notnull
{
    Task<JsonElement?> HandleAsync(TPayload payload, CancellationToken cancellationToken = default);
}
