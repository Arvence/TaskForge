namespace TaskForge.SDK.Handlers;

public sealed class JobResultSerializationException(string message, Exception? innerException = null) : Exception(message, innerException);
