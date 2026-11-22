# WoWiki × TaskForge integration brief

Verified against TaskForge `ed85633`. This brief covers the existing HTTP contract and the decisions WoWiki owns.

## Deployment and authentication

- The NestJS backend submits jobs and runs the worker. TaskForge persists and assigns jobs, manages attempts, and records outcomes. WoWiki owns News content, operation dispatch, and publication. The worker initiates every protocol request.
- Provision `JobAccess__Applications__wowiki__ApiKey` in the TaskForge deployment. For the default Compose setup, put it in `.taskforge-apps.env` and recreate the API container after adding it. Use a unique secret of 32–128 ASCII letters, digits, hyphens, or underscores.
- Configure a TaskForge base URL reachable from NestJS. The default host endpoint is `http://localhost:8275` and binds to host loopback. Keep the key server-side and use HTTPS across hosts.
- Send `Authorization: Bearer <WOWIKI_API_KEY>` on every job and attempt request and `Content-Type: application/json` on requests with a JSON body. Supply `applicationId: "wowiki"` explicitly on submit, wait, complete, and fail. Application IDs are trimmed and lowercased. A missing or invalid key returns `401`, a valid key paired with a different valid application ID returns `403`, and looking up or managing another application's job by ID returns `404`.
- `GET /api/ready` checks SQL connectivity. `GET /openapi/v1.json` serves the runtime schema. Both are public.

## 1. Submit a scheduled News job

Send `POST /api/jobs` with the following shape. Replace the timestamp placeholder with the selected instant in ISO 8601, for example by serializing it to UTC with `Z`. TaskForge also accepts an explicit offset and normalizes the instant to UTC.

```json
{
  "applicationId": "wowiki",
  "scheduledAtUtc": "<SCHEDULED_AT_ISO_8601_UTC>",
  "payload": {
    "operation": "publish-news",
    "newsId": "<NEWS_ID>",
    "scheduleId": "<SCHEDULE_ID>"
  }
}
```

- Omitting `type` selects `general`. `payload` is required and may be any JSON value, including explicit `null`. The server enforces 16 KiB of UTF-8 payload, JSON depth, and unique object keys. WoWiki requires the object shape shown above. Keep News content and credentials out of the payload.
- `priority` defaults to `Normal` and supports `Low`, `High`, and `Critical` as well. `maxRetries` accepts 0–10 and defaults to 3. `timeoutSeconds` accepts 1–3,600 and defaults to 30 per attempt.
- Send the same `Idempotency-Key` for each submission of the same schedule, for example `publish-news:<SCHEDULE_ID>` if it fits. Keys are case-sensitive, trimmed, 1–100 characters, and scoped to the application. `201` creates a job, `200` returns the existing job, and `409` means the key is already used for different submission content. The comparison includes raw payload JSON text, the normalized scheduled instant, type, priority, retry limit, and timeout. Preserve the serialized request for a manual retry after an uncertain response.
- Store the returned `id` as `taskForgeJobId` for the matching `scheduleId`. A new job is `Queued` even when scheduled for the future. It becomes eligible at the requested time, subject to compatible worker availability and queue ordering. Past schedules are eligible immediately. There is no job expiry based on `scheduledAtUtc`.

## 2. Acquire and execute

Each free NestJS execution slot sends `POST /api/executions/wait` with this body. Generate a unique `workerId` per process instance and slot, up to 200 nonblank characters.

```json
{
  "applicationId": "wowiki",
  "workerId": "<INSTANCE_AND_SLOT_ID>",
  "supportedTypes": ["general"],
  "waitSeconds": 20
}
```

- `waitSeconds` defaults to 20 and accepts 0–30. Zero makes a single acquisition attempt. Allow more time than the requested wait in the Node HTTP client and any proxy. `204` has an empty body. A `200` assignment contains `jobId`, `applicationId`, `attemptId`, `attemptNumber`, `workerId`, `type`, `payload`, `timeoutSeconds`, `startedAtUtc`, `deadlineAtUtc`, and `leaseExpiresAtUtc`.
- A claim moves the job to `Processing` and atomically creates a `Running` attempt in SQL Server. Only due, compatible jobs are eligible. Priority is applied before eligibility time. Keep one outstanding acquisition per free slot. A lost wait response may already have committed a claim. Back off and let TaskForge recover it on timeout. A later wait is a new acquisition.
- Dispatch `payload.operation` inside NestJS. TaskForge routes by application and type, so every WoWiki worker advertising `general` must support the operations submitted to that type. Validate the returned application and worker ID before dispatch. The assignment omits `scheduledAtUtc`. Read the current schedule from the News record.
- Use `deadlineAtUtc` as the attempt cutoff. The budget starts at acquisition and includes response transit time. Keep the worker clock synchronized. `leaseExpiresAtUtc` includes additional recovery grace, 30 seconds by default. TaskForge has neither a heartbeat nor a lease-renewal endpoint. Job reads never extend ownership.
- Poll `GET /api/jobs/{jobId}` during active work to observe cancellation or a changed assignment. Stop if `cancellationRequested` is true, `status` leaves `Processing`, or `owningWorkerId` or `startedAtUtc` no longer matches. Use a local deadline and cooperative abort. A status read is a snapshot. The publication write still needs WoWiki's own atomic guards.

## 3. Report the attempt

| Method and path | Body |
| --- | --- |
| `POST /api/jobs/{jobId}/attempts/{attemptId}/complete` | `applicationId`, exact `workerId`, optional `result`. Omit it or send `null` for no result. A serialized JSON result may be at most 4,000 characters. |
| `POST /api/jobs/{jobId}/attempts/{attemptId}/fail` | `applicationId`, exact `workerId`, `errorCode` (1–100 characters), and `errorMessage` (1–4,000). Use the acquired job and attempt IDs in the path. |

- For an expired WoWiki publication window or an unknown operation, send `errorCode: "NonRetryableJobException"` and a concise reason in `errorMessage`. `InvalidPayload` and `UnsupportedJobType` are also permanent. The server trims and lowercases the code. A permanent report sets `PermanentlyFailed` on the attempt and `DeadLettered` on the job without consuming a retry. A custom `PublicationDeadlineExceeded` code would be retryable.
- For a transient execution failure, send a code such as `NewsStoreUnavailable`. TaskForge sets the retry time and budget. Default backoff starts at 5 seconds and doubles up to 300 seconds. An automatic retry keeps the job ID and creates a new attempt when acquired. An exhausted retry budget ends in `DeadLettered`.
- `200` returns the current job for an accepted or identical previously recorded report. An identical historical failure can be acknowledged even when a later attempt is active. After a transport error, `408`, `429`, or `5xx`, retry the same report with the same identity and body within the remaining deadline. Check the job and attempt history if the outcome stays uncertain. `400`, `404`, or `409` require stopping report retries. A deadline conflict can include `code: "AttemptTimedOut"`. The API uses multiple error body shapes, so branch on HTTP status first.

## 4. Inspect, cancel, and recover

- `GET /api/jobs/{id}` returns `status`, `scheduledAtUtc`, `result`, `lastError`, `retryCount`, `nextRetryAtUtc`, ownership, and timestamps. `GET /api/jobs/{id}/attempts` returns attempts in ascending number, including `attemptId`, `workerId`, `outcome`, `deadlineAtUtc`, and any recorded error. There is no `Failed` job status. Terminal states are `Completed`, `DeadLettered`, and `Cancelled`.
- `POST /api/jobs/{id}/cancel` takes no body. Accepted cancellation returns `200` and also closes an active, unexpired attempt. Repeating cancellation of a cancelled job returns `200`. A missing job returns `404`, and a completed or dead-lettered job returns `409`. In a race, completion or an exhausted timeout may win. The API cannot interrupt an ongoing WoWiki database write.
- There is no in-place reschedule. Invalidate the old schedule in WoWiki, cancel its job best-effort, and submit a fresh `scheduleId` and key. `POST /api/jobs/{id}/retry` is an operator replay of a cancelled or dead-lettered job. It creates a new job ID without retaining the old schedule or idempotency key. Use a new WoWiki scheduling operation for editor rescheduling.
- With the same SQL database after restart, queued jobs remain eligible once due. Server maintenance closes unreported attempts after the deadline and makes retries available when due. Stop acquiring on shutdown and let recovery handle work that cannot finish within its attempt deadline.

## WoWiki publication invariants

1. Save the draft and reserve a schedule ID before submitting. After `201` or `200`, associate the job and mark the News scheduled only if that schedule ID is still current and the returned job can still run. A definite rejection stays visible to the editor. For an uncertain response, keep the pending schedule identity and reuse its key and serialized body on manual retry. If the worker claims that same schedule before WoWiki records registration, it should defer via a retryable failure without publishing. Resending the original submit request can return a terminal job. Show that outcome to the editor.
2. At the publication transaction, atomically require the expected News state, `scheduleId`, and `taskForgeJobId`. Invalidate old schedules locally before relying on cancellation for unscheduling, rescheduling, or Publish Now. Use the same publication service for Publish Now and the worker. A repeated attempt for a schedule that already published can acknowledge success without repeating side effects.
3. Interpret editor input in `Europe/Berlin` and store the resolved UTC instant. Reject nonexistent local times and require a disambiguated instant for repeated local times. At the final publication write, apply the optional WoWiki deadline of 30 minutes after the stored scheduled instant. If a still-unpublished item is beyond that window, send the permanent failure above. Ordinary jobs can publish late.
4. Treat execution as at-least-once across retries and recovery. TaskForge idempotency protects submission and outcome reporting. The WoWiki transaction protects publication. A TaskForge terminal failure can leave News marked scheduled until WoWiki explicitly reconciles the two states.

## References

- [HTTP API guide](https://github.com/Arvence/TaskForge/blob/ed856335bb9bd85523871da343b25d3e8b049e5b/docs/http-api.md)
- [Application access and payload validation](https://github.com/Arvence/TaskForge/blob/ed856335bb9bd85523871da343b25d3e8b049e5b/docs/payload-contracts.md)
- [Execution ownership, deadlines, and retry semantics](https://github.com/Arvence/TaskForge/blob/ed856335bb9bd85523871da343b25d3e8b049e5b/docs/architecture.md#ownership-deadlines-and-retries)
