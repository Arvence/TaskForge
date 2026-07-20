using System.Collections.Concurrent;
using TaskForge.Domain.Jobs;

namespace TaskForge.Api.Jobs;

public sealed class InMemoryJobStore
{
    private readonly ConcurrentDictionary<Guid, Job> _jobs = new();

    public void Add(Job job)
    {
        if (!_jobs.TryAdd(job.Id, job))
        {
            throw new InvalidOperationException($"Job '{job.Id}' already exists.");
        }
    }

    public bool TryGet(Guid id, out Job job) => _jobs.TryGetValue(id, out job!);

    public IReadOnlyCollection<Job> GetAll() =>
        [.. _jobs.Values.OrderByDescending(job => job.CreatedAtUtc)];
}
