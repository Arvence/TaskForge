namespace TaskForge.Application.Abstractions.Execution;

public sealed class NonRetryableJobException(string message, Exception? innerException = null) : Exception(message, innerException)
{
}
