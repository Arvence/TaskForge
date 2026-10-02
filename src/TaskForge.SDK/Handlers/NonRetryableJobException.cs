namespace TaskForge.SDK.Handlers;

public sealed class NonRetryableJobException(string message, Exception? innerException = null) : Exception(message, innerException);
