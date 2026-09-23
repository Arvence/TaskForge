namespace TaskForge.Application.Jobs.Models;

public enum ExecutionResult
{
    Accepted,
    Duplicate,
    Missing,
    TimedOut,
    Cancelled,
    Stale,
    Conflicting
}

public sealed record ExecutionLookup(ExecutionResult Result, JobExecutionAssignment? Assignment = null);
