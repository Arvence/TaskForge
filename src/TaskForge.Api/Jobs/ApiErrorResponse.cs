using System.Text.Json.Serialization;

using TaskForge.Domain.Jobs;

namespace TaskForge.Api.Jobs;

public sealed record ApiErrorResponse(string Message);

public sealed record IdempotencyConflictResponse(string Message, string IdempotencyKey);

public sealed record JobConflictResponse(string Message, [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] JobStatus? Status = null);
