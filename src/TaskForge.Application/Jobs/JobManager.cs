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

    public Task<IReadOnlyList<Job>> GetAllAsync(
        CancellationToken cancellationToken = default) =>
        jobRepository.GetAllAsync(cancellationToken);

    public Task<Job?> GetByIdAsync(
        Guid id,
        CancellationToken cancellationToken = default) =>
        jobRepository.FindAsync(id, cancellationToken);

    private static bool HasSameSubmission(Job existing, Job candidate) =>
        existing.Type == candidate.Type
        && existing.PayloadJson == candidate.PayloadJson
        && existing.Priority == candidate.Priority
        && existing.MaxRetries == candidate.MaxRetries
        && existing.TimeoutSeconds == candidate.TimeoutSeconds;
}