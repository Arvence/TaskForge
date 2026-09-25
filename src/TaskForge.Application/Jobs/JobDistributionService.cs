using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

using TaskForge.Application.Abstractions.Execution;
using TaskForge.Application.Common.Exceptions;
using TaskForge.Application.Jobs.Models;
using TaskForge.Application.Workers;
using TaskForge.Domain.Jobs;

namespace TaskForge.Application.Jobs;

public sealed class JobDistributionService(IServiceScopeFactory scopeFactory, TimeProvider timeProvider, IOptions<WorkerOptions> options, IHostApplicationLifetime lifetime)
{
    public const int MaximumSupportedTypes = 100;
    public const int DefaultWaitSeconds = 20;
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(500);

    public async Task<JobExecutionAssignment?> WaitAsync(string? applicationId, string? workerId, IReadOnlyCollection<string?>? supportedTypes, int waitSeconds = DefaultWaitSeconds, CancellationToken cancellationToken = default)
    {
        Validate(applicationId, workerId, supportedTypes, waitSeconds);
        string normalizedApplicationId = JobApplicationId.Normalize(applicationId!);
        string[] types = supportedTypes!.Select(type => type!).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        TimeSpan wait = TimeSpan.FromSeconds(waitSeconds);
        long started = timeProvider.GetTimestamp();
        using CancellationTokenSource deadline = new(waitSeconds == 0 ? Timeout.InfiniteTimeSpan : wait, timeProvider);
        using CancellationTokenSource stopping = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.ApplicationStopping, deadline.Token);
        try
        {
            bool firstPoll = true;
            while (firstPoll || timeProvider.GetElapsedTime(started) < wait)
            {
                firstPoll = false;
                stopping.Token.ThrowIfCancellationRequested();
                JobExecutionAssignment? assignment;
                await using (AsyncServiceScope scope = scopeFactory.CreateAsyncScope())
                {
                    IJobQueue queue = scope.ServiceProvider.GetRequiredService<IJobQueue>();
                    assignment = await queue.TryDistributeAsync(normalizedApplicationId, workerId!, types,
                        TimeSpan.FromSeconds(options.Value.LeaseGraceSeconds), timeProvider.GetUtcNow(), stopping.Token);
                }

                if (assignment is not null)
                {
                    return assignment;
                }

                TimeSpan remaining = wait - timeProvider.GetElapsedTime(started);
                if (remaining <= TimeSpan.Zero)
                {
                    return null;
                }

                await Task.Delay(remaining < PollInterval ? remaining : PollInterval, timeProvider, stopping.Token);
            }

            return null;
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested && !cancellationToken.IsCancellationRequested && !lifetime.ApplicationStopping.IsCancellationRequested)
        {
            return null;
        }
    }

    private static void Validate(string? applicationId, string? workerId, IReadOnlyCollection<string?>? supportedTypes, int waitSeconds)
    {
        Dictionary<string, string[]> errors = [];
        if (!JobApplicationId.IsValid(applicationId))
        {
            errors["ApplicationId"] = ["Application ID must contain 1 to 100 ASCII letters, digits, dots, underscores, or hyphens."];
        }

        if (string.IsNullOrWhiteSpace(workerId) || workerId.Length > 200)
        {
            errors["WorkerId"] = ["Worker ID must contain 1 to 200 characters and cannot be blank."];
        }

        if (supportedTypes is null || supportedTypes.Count is < 1 or > MaximumSupportedTypes
            || supportedTypes.Any(type => string.IsNullOrWhiteSpace(type) || type.Length > 100))
        {
            errors["SupportedTypes"] = [$"Provide 1 to {MaximumSupportedTypes} supported types, each containing 1 to 100 characters and not blank."];
        }

        if (waitSeconds is < 0 or > 30)
        {
            errors["WaitSeconds"] = ["Wait seconds must be between 0 and 30."];
        }

        if (errors.Count > 0)
        {
            throw new ApplicationValidationException(errors);
        }
    }
}
