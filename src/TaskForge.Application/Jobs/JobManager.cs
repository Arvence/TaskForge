using TaskForge.Application.Abstractions.Persistence;
using TaskForge.Application.Common.Exceptions;
using TaskForge.Application.Jobs.Models;
using TaskForge.Application.Jobs.Validation;
using TaskForge.Domain.Jobs;

namespace TaskForge.Application.Jobs;

public sealed class JobManager(
    IJobRepository jobRepository,
    SubmitJobValidator submitJobValidator,
    TimeProvider timeProvider)
{
    public async Task<Job> SubmitAsync(
        SubmitJobCommand command,
        CancellationToken cancellationToken = default) =>
        (await SubmitAsync(command, null, cancellationToken)).Job;

    public async Task<JobSubmissionResult> SubmitAsync(
        SubmitJobCommand command,
        string? idempotencyKey,
        CancellationToken cancellationToken = default)
    {
        IReadOnlyDictionary<string, string[]> errors =
            submitJobValidator.Validate(command, idempotencyKey);
        if (errors.Count > 0)
        {
            throw new ApplicationValidationException(errors);
        }

        DateTimeOffset now = timeProvider.GetUtcNow();
        string? normalizedIdempotencyKey = idempotencyKey?.Trim();
        Job job = new(
            Guid.NewGuid(),
            command.ApplicationId,
            command.Type.Trim(),
            command.PayloadJson,
            command.Priority,
            command.MaxRetries,
            command.TimeoutSeconds,
            now,
            normalizedIdempotencyKey);

        job.Queue(now);
        Job persistedJob = await jobRepository.AddOrGetExistingAsync(
            job,
            cancellationToken);

        bool created = persistedJob.Id == job.Id;
        if (!created && !HasSameSubmission(persistedJob, job))
        {
            throw new IdempotencyConflictException(normalizedIdempotencyKey!);
        }

        return new JobSubmissionResult(persistedJob, created);
    }

    public async Task<JobReplayResult> ReplayAsync(Guid id, CancellationToken cancellationToken = default)
    {
        Job? source = await jobRepository.FindAsync(id, cancellationToken);
        if (source is null)
        {
            return new JobReplayResult(JobReplayStatus.NotFound, null);
        }

        if (source.Status is not (JobStatus.DeadLettered or JobStatus.Cancelled))
        {
            return new JobReplayResult(JobReplayStatus.InvalidState, source);
        }

        SubmitJobCommand command = new(source.ApplicationId, source.Type, source.PayloadJson, source.Priority, source.MaxRetries, source.TimeoutSeconds);
        JobSubmissionResult replay = await SubmitAsync(command, idempotencyKey: null, cancellationToken);
        return new JobReplayResult(JobReplayStatus.Created, replay.Job);
    }

    public Task<JobPage> GetPageAsync(
        ListJobsQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        Dictionary<string, string[]> errors = [];
        if (query.ApplicationId is not null && !JobApplicationId.IsValid(query.ApplicationId))
        {
            errors["ApplicationId"] = ["Application ID must contain 1 to 100 ASCII letters, digits, dots, underscores, or hyphens."];
        }

        if (query.Status is not null && !Enum.IsDefined(query.Status.Value))
        {
            errors["Status"] = ["Job status is invalid."];
        }

        if (query.Priority is not null && !Enum.IsDefined(query.Priority.Value))
        {
            errors["Priority"] = ["Job priority is invalid."];
        }

        if (query.Type is not null && string.IsNullOrWhiteSpace(query.Type))
        {
            errors["Type"] = ["Job type cannot be blank."];
        }

        if (query.Page < 1)
        {
            errors["Page"] = ["Page must be at least 1."];
        }

        if (query.PageSize is < 1 or > ListJobsQuery.MaximumPageSize)
        {
            errors["PageSize"] =
            [
                $"Page size must be between 1 and "
                + $"{ListJobsQuery.MaximumPageSize}."
            ];
        }

        if (errors.Count > 0)
        {
            throw new ApplicationValidationException(errors);
        }

        ListJobsQuery normalizedQuery = query with
        {
            ApplicationId = query.ApplicationId is null ? null : JobApplicationId.Normalize(query.ApplicationId),
            Type = query.Type?.Trim()
        };
        return jobRepository.GetPageAsync(normalizedQuery, cancellationToken);
    }

    public Task<Job?> GetByIdAsync(
        Guid id,
        CancellationToken cancellationToken = default) =>
        jobRepository.FindAsync(id, cancellationToken);

    private static bool HasSameSubmission(Job existing, Job candidate) =>
        existing.ApplicationId == candidate.ApplicationId
        && existing.Type == candidate.Type
        && existing.PayloadJson == candidate.PayloadJson
        && existing.Priority == candidate.Priority
        && existing.MaxRetries == candidate.MaxRetries
        && existing.TimeoutSeconds == candidate.TimeoutSeconds;
}
