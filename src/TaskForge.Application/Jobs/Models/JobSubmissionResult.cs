using TaskForge.Domain.Jobs;

namespace TaskForge.Application.Jobs.Models;

public sealed record JobSubmissionResult(Job Job, bool Created);