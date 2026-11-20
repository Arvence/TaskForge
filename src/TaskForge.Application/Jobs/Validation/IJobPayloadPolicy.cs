using System.Text.Json;

namespace TaskForge.Application.Jobs.Validation;

public interface IJobPayloadPolicy
{
    string? Validate(string applicationId, string jobType, JsonElement payload, int payloadBytes);
}
