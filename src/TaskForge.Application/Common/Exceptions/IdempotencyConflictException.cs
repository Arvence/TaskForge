namespace TaskForge.Application.Common.Exceptions;

public sealed class IdempotencyConflictException(string idempotencyKey)
    : Exception(
        $"Idempotency key '{idempotencyKey}' was already used for a different job.")
{
    public string IdempotencyKey { get; } = idempotencyKey;
}