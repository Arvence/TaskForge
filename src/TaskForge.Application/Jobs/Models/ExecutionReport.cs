namespace TaskForge.Application.Jobs.Models;

public abstract record ExecutionReport
{
    private ExecutionReport() { }

    public sealed record Complete(string? ResultJson = null) : ExecutionReport;

    public sealed record Fail : ExecutionReport
    {
        public Fail(string errorCode, string errorMessage, bool retryable = true)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(errorCode);
            ArgumentException.ThrowIfNullOrWhiteSpace(errorMessage);
            ErrorCode = errorCode.Length > 100 ? errorCode[..100] : errorCode;
            ErrorMessage = errorMessage.Length > 4000 ? errorMessage[..4000] : errorMessage;
            Retryable = retryable;
        }

        public string ErrorCode { get; }
        public string ErrorMessage { get; }
        public bool Retryable { get; }
    }

    public sealed record Timeout : ExecutionReport;

    public sealed record Cancel : ExecutionReport;
}
