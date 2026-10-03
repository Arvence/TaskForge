using System.Text.Json;

using Microsoft.Extensions.Logging;

using TaskForge.SDK.Execution;
using TaskForge.SDK.Handlers;

namespace TaskForge.SampleClient.Handlers;

public sealed class ReportExecutionHandler(GenerateReportJobHandler report, JobExecutionContext context, ILogger<ReportExecutionHandler> logger) : IJobHandler<GenerateReportPayload>
{
    public Task<JsonElement?> HandleAsync(GenerateReportPayload payload, CancellationToken cancellationToken = default)
    {
        logger.LogInformation("Generating report for application {ApplicationId}, job {JobId}, attempt {AttemptNumber} ({AttemptId}), worker {WorkerId}.",
            context.ApplicationId, context.JobId, context.AttemptNumber, context.AttemptId, context.WorkerId);
        return report.HandleAsync(payload, cancellationToken);
    }
}
