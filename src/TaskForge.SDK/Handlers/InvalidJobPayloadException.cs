namespace TaskForge.SDK.Handlers;

public sealed class InvalidJobPayloadException(string jobType, Type payloadType, Exception innerException) : Exception($"Payload for job type '{jobType}' cannot be deserialized as '{payloadType}'.", innerException)
{
    public string JobType { get; } = jobType;
    public Type PayloadType { get; } = payloadType;
}
