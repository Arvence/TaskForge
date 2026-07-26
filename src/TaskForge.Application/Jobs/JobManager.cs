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
        CancellationToken cancellationToken = default)
    {
        IReadOnlyDictionary<string, string[]> errors =
            submitJobValidator.Validate(command);
        if (errors.Count > 0)
        {
            throw new ApplicationValidationException(errors);
        }

        DateTimeOffset now = timeProvider.GetUtcNow();
        Job job = new(
            Guid.NewGuid(),
            command.Type.Trim(),
            command.PayloadJson,
            command.Priority,
            command.MaxRetries,
            command.TimeoutSeconds,
            now);

        job.Queue(now);
        await jobRepository.AddAsync(job, cancellationToken);

        return job;
    }

    public Task<IReadOnlyList<Job>> GetAllAsync(
        CancellationToken cancellationToken = default) =>
        jobRepository.GetAllAsync(cancellationToken);

    public Task<Job?> GetByIdAsync(
        Guid id,
        CancellationToken cancellationToken = default) =>
        jobRepository.FindAsync(id, cancellationToken);
}
