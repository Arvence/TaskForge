using TaskForge.Domain.Jobs;

namespace TaskForge.Application.Jobs.Models;

public sealed record ExecutionReportResult(ExecutionResult Status, Job? Job);
