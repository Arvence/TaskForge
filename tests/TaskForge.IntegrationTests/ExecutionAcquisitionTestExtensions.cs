using System.Data;
using System.Data.Common;

using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore.Diagnostics;

using TaskForge.Application.Abstractions.Execution;
using TaskForge.Application.Jobs.Models;

namespace TaskForge.IntegrationTests;

internal static class ExecutionAcquisitionTestExtensions
{
    private static readonly AsyncLocal<DateTimeOffset?> CurrentTime = new();
    public static IInterceptor Clock { get; } = new AcquisitionClock();

    public static async Task<JobExecutionAssignment?> DistributeAtAsync(this IJobQueue queue, string applicationId, string workerId, IReadOnlyCollection<string> supportedTypes, TimeSpan leaseGracePeriod, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        DateTimeOffset? previous = CurrentTime.Value;
        CurrentTime.Value = now;
        try
        {
            return await queue.TryDistributeAsync(applicationId, workerId, supportedTypes, leaseGracePeriod, cancellationToken);
        }
        finally
        {
            CurrentTime.Value = previous;
        }
    }

    private sealed class AcquisitionClock : DbCommandInterceptor
    {
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (CurrentTime.Value is { } now && command.CommandText.Contains("SYSUTCDATETIME()", StringComparison.Ordinal))
            {
                command.CommandText = command.CommandText.Replace("SYSUTCDATETIME()", "@acquisitionSqlUtc", StringComparison.Ordinal);
                command.Parameters.Add(new SqlParameter("@acquisitionSqlUtc", SqlDbType.DateTime2) { Value = now.UtcDateTime, Scale = 7 });
            }

            return ValueTask.FromResult(result);
        }
    }
}
