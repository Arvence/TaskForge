using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

using TaskForge.Domain.Jobs;
using TaskForge.Infrastructure.Persistence;

namespace TaskForge.IntegrationTests.Persistence;

[Collection("SQL Server")]
public sealed class SqlServerApplicationOwnershipMigrationTests(SqlServerFixture fixture) : SqlServerTest(fixture)
{
    [Fact]
    public async Task Migration_backfills_legacy_ownership_and_preserves_jobs_and_history_without_a_default()
    {
        await using TaskForgeDbContext context = new(DatabaseOptions);
        IMigrator migrator = context.GetService<IMigrator>();
        await migrator.MigrateAsync("20260911113722_InitialSqlServer");
        Guid completedId = Guid.NewGuid();
        Guid queuedId = Guid.NewGuid();
        Guid attemptId = Guid.NewGuid();
        DateTimeOffset started = new(2026, 9, 20, 12, 0, 0, TimeSpan.Zero);
        DateTimeOffset finished = started.AddSeconds(1);
        await context.Database.ExecuteSqlInterpolatedAsync($$"""
            INSERT INTO [Jobs] ([Id], [Type], [PayloadJson], [IdempotencyKey], [ResultJson], [Priority], [Status],
                [MaxRetries], [RetryCount], [TimeoutSeconds], [CancellationRequested], [CreatedAtUtc], [UpdatedAtUtc],
                [QueuedAtUtc], [StartedAtUtc], [CompletedAtUtc], [Version])
            VALUES ({{completedId}}, N'example', N'{"value":1}', N'original-key', N'{"ok":true}', 2, N'Completed',
                3, 1, 30, 0, {{started}}, {{finished}}, {{started}}, {{started}}, {{finished}}, 7),
                ({{queuedId}}, N'example', N'{}', NULL, NULL, 1, N'Queued', 3, 0, 30, 0, {{started}}, {{started}},
                {{started}}, NULL, NULL, 1);
            INSERT INTO [JobAttempts] ([Id], [JobId], [AttemptNumber], [WorkerId], [Outcome], [StartedAtUtc],
                [FinishedAtUtc], [DurationMilliseconds], [ErrorCode], [ErrorMessage])
            VALUES ({{attemptId}}, {{completedId}}, 1, N'original-worker', N'Succeeded', {{started}}, {{finished}}, 1000, NULL, NULL);
            """);

        await migrator.MigrateAsync();

        Job completed = await context.Jobs.AsNoTracking().SingleAsync(job => job.Id == completedId);
        Job queued = await context.Jobs.AsNoTracking().SingleAsync(job => job.Id == queuedId);
        Assert.Equal("legacy", completed.ApplicationId);
        Assert.Equal("legacy", queued.ApplicationId);
        Assert.Equal(JobStatus.Completed, completed.Status);
        Assert.Equal(JobStatus.Queued, queued.Status);
        Assert.Equal("original-key", completed.IdempotencyKey);
        Assert.Null(queued.IdempotencyKey);
        Assert.Equal("{\"value\":1}", completed.PayloadJson);
        Assert.Equal("{\"ok\":true}", completed.ResultJson);
        Assert.Equal(JobPriority.High, completed.Priority);
        Assert.Equal(1, completed.RetryCount);
        Assert.Equal(3, completed.MaxRetries);
        Assert.Equal(30, completed.TimeoutSeconds);
        Assert.Equal(7, completed.Version);
        Assert.Equal(started, completed.CreatedAtUtc);
        Assert.Equal(finished, completed.UpdatedAtUtc);
        Assert.Equal(finished, completed.CompletedAtUtc);
        JobAttempt attempt = await context.JobAttempts.AsNoTracking().SingleAsync();
        Assert.Equal(attemptId, attempt.Id);
        Assert.Equal(completedId, attempt.JobId);
        Assert.Equal("original-worker", attempt.WorkerId);
        Assert.Equal(JobAttemptOutcome.Succeeded, attempt.Outcome);
        Assert.Equal(started, attempt.StartedAtUtc);
        Assert.Equal(finished, attempt.FinishedAtUtc);
        Assert.Equal(1000, attempt.DurationMilliseconds);

        var history = await new EfCoreJobStore(context).GetAttemptsAsync(completedId);
        Assert.NotNull(history);
        Assert.Equal(30, history.TimeoutSeconds);
        JobAttempt historicalAttempt = Assert.Single(history.Attempts);
        var response = TaskForge.Api.Jobs.JobAttemptResponse.From(historicalAttempt, history.TimeoutSeconds);
        Assert.Equal(attemptId, response.AttemptId);
        Assert.Equal(started.AddSeconds(30), response.DeadlineAtUtc);
        Assert.Equal(finished, response.FinishedAtUtc);

        Guid missingOwnerId = Guid.NewGuid();
        SqlException exception = await Assert.ThrowsAsync<SqlException>(() => context.Database.ExecuteSqlInterpolatedAsync($$"""
            INSERT INTO [Jobs] ([Id], [Type], [PayloadJson], [Priority], [Status], [MaxRetries], [RetryCount],
                [TimeoutSeconds], [CancellationRequested], [CreatedAtUtc], [UpdatedAtUtc], [Version])
            VALUES ({{missingOwnerId}}, N'example', N'{}', 1, N'Queued', 3, 0, 30, 0, {{started}}, {{started}}, 0);
            """));
        Assert.Equal(515, exception.Number);
        Assert.Equal(2, await context.Jobs.CountAsync());

        Job other = new(Guid.NewGuid(), "new-app", "example", "{}", JobPriority.Normal, 3, 30, started, "original-key");
        await new EfCoreJobStore(context).AddAsync(other);
        Assert.Equal(3, await context.Jobs.CountAsync());
    }
}
