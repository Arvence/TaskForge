# Guarded execution transitions

`IExecutionStore` is the persistence boundary for remote execution lifecycle changes. TaskForge manages job and attempt state; the client application executes the job. This boundary does not expose an HTTP endpoint or invoke a handler.

An `ExecutionIdentity` contains the normalized application ID, job ID, persisted attempt GUID, and worker ID. It contains no client timestamp, job version, or requested job status. Worker IDs are compared exactly. `ExecutionReport` provides Complete, Fail, Timeout, and Cancel operations. TaskForge chooses the resulting job and attempt states and calculates retries with `JobRetryPolicy`.

`FindExecutionAsync` returns a detached snapshot for inspection. A snapshot is not authorization for a later update: `TransitionAsync` always reloads and validates the identity, latest attempt, ownership, start time, and current state in its own transaction. It clears stale tracked entities before each attempt.

## Results

| Result | Meaning |
| --- | --- |
| Accepted | A valid Complete or Fail report was committed. On lookup, the execution is currently eligible to report. |
| Duplicate | The latest attempt already finished with the same completion result or failure outcome and stored error values. No write occurs. On lookup, this identifies a finished success/failure; report equivalence has not yet been checked. |
| Missing | The application/job/attempt combination does not exist, including an attempt paired with another job. |
| TimedOut | The deadline has been reached. A running attempt is atomically timed out and its job retried or dead-lettered; an already timed-out attempt is left unchanged. Lookup alone never performs that transition. |
| Cancelled | Cancellation has been requested or recorded. A running attempt and job are atomically cancelled; finished attempts remain unchanged. Lookup alone never performs that transition. |
| Stale | A newer attempt exists, the attempt was abandoned, or the running attempt no longer matches active job ownership/state. |
| Conflicting | The worker is wrong, a finished report differs, timeout/cancellation is premature, or bounded conflict retries were exhausted. |

Cancellation takes precedence over timeout and client reports. Without cancellation, the execution deadline is `attempt.StartedAtUtc + job.TimeoutSeconds`; the lease grace period does not extend the reporting deadline. Timeout and Cancel reports cannot force those states before the server conditions hold. Exact repeated completion results use string equality; differently serialized JSON is not automatically treated as an equivalent report.

## Persistence and races

The SQL Server implementation holds update locks on the job and attempt until the transaction ends. It checks SQL UTC after acquiring those locks, then checks a fresh SQL UTC value in the conditional job update. A write delayed across the deadline cannot commit a success or ordinary failure. Its transaction is rolled back, and the execution's state and deadline are evaluated again from fresh data.

The conditional job update checks the expected server-read `Job.Version`, identity, ownership, running attempt, cancellation condition, and deadline. Its SQL timestamp is also used for the attempt finish time and the next retry time. The attempt update requires `Running`, occurs only after the guarded job update succeeds, and commits in the same transaction. A failure in either write rolls back both. Once this conditional job update succeeds, it is the transition's time boundary; the transaction retains its locks until the attempt write and commit finish.

Conflicts are retried at most five times by the transition operation, with fresh reads on each attempt. SQL Server's configured execution strategy also handles transient connection failures. No fallback independently updates an attempt after a rejected job transition.

The legacy embedded executor still uses `IJobQueue` while the client execution migration is in progress. Remote lifecycle code must use `IExecutionStore`; the legacy mutable-entity update APIs are not its persistence boundary.
