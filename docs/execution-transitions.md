# Guarded execution transitions

`IExecutionStore` is the persistence boundary for remote execution lifecycle changes. TaskForge manages job and attempt state; the client application executes the job. This boundary does not invoke a handler. Complete and Fail reporting have opt-in HTTP registration; normal startup does not register the incomplete remote protocol.

An `ExecutionIdentity` contains the normalized application ID, job ID, persisted attempt GUID, and worker ID. It contains no client timestamp, job version, or requested job status. Worker IDs are compared exactly. `ExecutionReport` provides Complete, Fail, Timeout, and Cancel operations. TaskForge chooses the resulting job and attempt states and calculates retries with `JobRetryPolicy`.

`FindExecutionAsync` returns a detached snapshot for inspection. A snapshot is not authorization for a later update: `TransitionAsync` always reloads and validates the identity in its own transaction. New transitions require the latest attempt, ownership, start time, and current state to match. Recorded failures can be acknowledged by their original attempt identity even after redistribution. It clears stale tracked entities before each attempt.

## Results

| Result | Meaning |
| --- | --- |
| Accepted | A valid Complete or Fail report was committed. On lookup, the execution is currently eligible to report. |
| Duplicate | The latest attempt already succeeded with the same completion result, or the identified attempt already failed with the same normalized error fields, even if a newer attempt exists. No write occurs. On lookup, this identifies a finished success/failure; report equivalence has not yet been checked. |
| Missing | The application/job/attempt combination does not exist, including an attempt paired with another job. |
| TimedOut | The deadline has been reached. A running attempt is atomically timed out and its job retried or dead-lettered; an already timed-out attempt is left unchanged. Lookup alone never performs that transition. |
| Cancelled | Cancellation has been requested or recorded. A running attempt and job are atomically cancelled; finished attempts remain unchanged. Lookup alone never performs that transition. |
| Stale | A newer attempt exists without an accepted failure to compare, the attempt was abandoned, or the running attempt no longer matches active job ownership/state. |
| Conflicting | The worker is wrong, a finished report differs, timeout/cancellation is premature, or bounded conflict retries were exhausted. |

Cancellation takes precedence over timeout and client reports. Without cancellation, the execution deadline is `attempt.StartedAtUtc + job.TimeoutSeconds`; the lease grace period does not extend the reporting deadline. Timeout and Cancel reports cannot force those states before the server conditions hold. An already accepted completion is compared before checking its original deadline, so an identical retry remains successful after that deadline.

## Complete reporting

`AddTaskForgeExecutionReporting` registers the Application service and `MapTaskForgeExecutionEndpoints` maps `POST /api/jobs/{jobId}/attempts/{attemptId}/complete`. These registrations are deliberately absent from `Program.cs`. An opt-in host must also supply SQL Server persistence and `JobRetryPolicy`.

The request contains `applicationId`, `workerId`, and an optional JSON `result`:

```json
{
  "applicationId": "billing",
  "workerId": "worker-01",
  "result": { "processed": 42 }
}
```

Application IDs use the existing trim/lowercase normalization. Worker IDs are required, contain at most 200 characters, and match exactly, including case and whitespace. These identifiers validate execution ownership; they are not authentication credentials. Job and attempt IDs must be nonempty GUIDs.

Omitting `result` and sending JSON `null` both store a null result. Other JSON values are compacted with `System.Text.Json` before comparison and persistence. Whitespace outside strings and equivalent string escapes normalize consistently; object property order, array order, and number spelling remain significant. Comparison uses ordinal equality of that normalized serialized result. The normalized string must fit the existing 4,000-character storage limit, including JSON quotes and escapes. Invalid or oversized results are rejected before persistence and are never truncated. This validation also applies to direct `ExecutionReport.Complete` construction.

First acceptance and identical duplicates return `200` with the existing `JobResponse`, including the JSON result. Duplicates perform no writes to job timestamps, version, retry counters, result, or attempt history. Invalid identity input, malformed JSON, and oversized results return `400`; a missing application/job/attempt combination returns `404`. Wrong workers, conflicting reports, timed-out, stale, and cancelled executions return `409`. A first report past the deadline cannot store success; the existing guarded transition records the authoritative timeout (or cancellation) when needed.

## Fail reporting

The same opt-in registration maps `POST /api/jobs/{jobId}/attempts/{attemptId}/fail`. Its request contains only `applicationId`, `workerId`, `errorCode`, and `errorMessage`:

```json
{
  "applicationId": "billing",
  "workerId": "worker-01",
  "errorCode": "DependencyUnavailable",
  "errorMessage": "The upstream service did not respond."
}
```

Identity validation matches Complete. Both error fields are required and must be nonblank. Codes are trimmed and lowercased invariantly; messages are trimmed while preserving internal whitespace and case. Normalized codes must fit 100 characters and messages must fit 4,000 characters. Oversized fields return `400` without truncation or persistence. The attempt stores both normalized fields in full. The existing combined `Job.LastError` display summary remains bounded to 4,000 characters; duplicate comparisons use the attempt fields, not that summary.

`JobFailurePolicy` owns this small permanent-error set:

| Code (case-insensitive input) | Meaning |
| --- | --- |
| InvalidPayload | The execution payload cannot be processed. |
| UnsupportedJobType | The client cannot execute this job type. |
| NonRetryableJobException | An explicitly permanent handler failure, matching the embedded executor's exception category. |

Every other code is retryable, including unknown codes and reported `Timeout` or `CancellationRequested` strings. These strings cannot select server timeout or cancellation states. The server first enforces actual cancellation and deadline conditions, then classifies an eligible failure. A permanent failure records `PermanentlyFailed` and calls `Job.DeadLetter` without consuming a retry. An ordinary failure records `Failed` and calls `Job.Fail`, which increments the retry count once and chooses `Retrying` or `DeadLettered` from the stored retry budget. `JobRetryPolicy` calculates the capped delay from the retry count before failure. The retry time is the SQL acceptance timestamp plus that delay, not the attempt start time, an earlier validation timestamp, or client time.

The DTO has no retryability, status, retry-count, budget, or retry-time controls. Extra JSON properties such as `retryable`, `status`, `retryCount`, or `nextRetryAtUtc` are ignored and never influence policy. No client handler is executed by this endpoint.

Accepted failures and exact normalized duplicates return `200` with the current `JobResponse`. A duplicate is checked against the original stored attempt after identity validation, before the original deadline or current assignment is considered. It remains valid after redistribution or completion of a later attempt. Acknowledgement does not reclassify the historical outcome, update timestamps/history, or consume another retry. The returned job may therefore be `Processing` or `Completed` when acknowledging an older failed attempt. Different normalized error fields, Complete/Fail conflicts, wrong workers, and unaccepted timed-out, cancelled, or stale reports return `409`. Missing identities return `404` and invalid fields return `400`.

## Persistence and races

The SQL Server implementation holds update locks on the job and attempt until the transaction ends. It checks SQL UTC after acquiring those locks, then checks a fresh SQL UTC value in the conditional job update. A write delayed across the deadline cannot commit a success or ordinary failure. Its transaction is rolled back, and the execution's state and deadline are evaluated again from fresh data.

The conditional job update checks the expected server-read `Job.Version`, identity, ownership, running attempt, cancellation condition, and deadline. Its SQL timestamp is also used for the attempt finish time and the next retry time. The attempt update requires `Running`, occurs only after the guarded job update succeeds, and commits in the same transaction. A failure in either write rolls back both. Once this conditional job update succeeds, it is the transition's time boundary; the transaction retains its locks until the attempt write and commit finish.

Conflicts are retried at most five times by the transition operation, with fresh reads on each attempt. SQL Server's configured execution strategy also handles transient connection failures. No fallback independently updates an attempt after a rejected job transition.

The legacy embedded executor still uses `IJobQueue` while the client execution migration is in progress. Remote lifecycle code must use `IExecutionStore`; the legacy mutable-entity update APIs are not its persistence boundary.
