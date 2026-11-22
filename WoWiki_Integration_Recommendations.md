# WoWiki × TaskForge integration recommendations

Proceed with the existing TaskForge Server over HTTP. These recommendations use the verified TaskForge contract at `ed85633` and the agreed WoWiki News scheduling design.

## Architecture

- Keep scheduling commands and the publication service in WoWiki's existing News module. Store `scheduledAtUtc`, `scheduleId`, `taskForgeJobId`, and `enforcePublicationDeadline` on the News record. Reuse its existing publication state. The deadline flag defaults to `false`.
- Add a small HTTP client and one lifecycle-managed worker loop inside NestJS. Start with one execution slot per backend instance that runs the worker. Dispatch through a small explicit map keyed by `payload.operation`, starting with `publish-news`.
- TaskForge owns job eligibility, assignment, retries, timeouts, and attempt history. WoWiki owns News content and the publication decision. All protocol calls originate in NestJS. TaskForge Server and SQL Server are the only TaskForge runtime dependencies. The TaskForge CLI and .NET SDK are optional.
- Provision `JobAccess__Applications__wowiki__ApiKey` in TaskForge. Keep the base URL and bearer key in backend configuration. Send `Authorization: Bearer <WOWIKI_API_KEY>` on job and attempt requests and `Content-Type: application/json` for JSON bodies. Include `applicationId: "wowiki"` explicitly in submit, wait, complete, and fail bodies. Use a URL reachable from NestJS and HTTPS across hosts.

## Schedule and manage News

1. **Schedule.** Interpret editor input in `Europe/Berlin` and resolve it to a UTC instant. Reject nonexistent local times and ask the editor to choose an unambiguous time when the clock repeats. Save the draft and reserve a fresh `scheduleId` before contacting TaskForge. Build the request deterministically from that schedule, keeping the inputs needed to reproduce it for a manual retry.
2. **Register.** Submit the request below with a stable `Idempotency-Key` such as `publish-news:<SCHEDULE_ID>`. A `201` creates the job and a `200` returns the existing job. Store its `id` and mark the News scheduled only if the reserved schedule is still current and the returned job can still run. If the schedule was superseded while the request was in flight, leave the newer state intact and cancel the returned job best-effort.
3. **Handle submission errors.** Keep News as a draft on definite rejection and show an actionable error. Treat network failures or ambiguous server errors as an unknown outcome. Keep the pending schedule and resend the same serialized request with the same key only when the editor explicitly retries. A `409` is a conflict with different submission content. A `200` can return a terminal job, so inspect its status before marking the News scheduled.
4. **Edit, reschedule, or unschedule.** Title, body, and media edits affect WoWiki. For rescheduling or unscheduling, atomically invalidate the old schedule in WoWiki first, then cancel the old TaskForge job best-effort. A new schedule gets a new ID and key. Unscheduling transitions the News to draft if publication has not already won the race.
5. **Publish Now.** Use the same publication service. Atomically publish and invalidate the previous schedule, then cancel its job best-effort. Repeated requests should leave the already-published record intact.

**`POST /api/jobs` request body**

```json
{
  "applicationId": "wowiki",
  "scheduledAtUtc": "<ISO_8601_UTC_TIMESTAMP>",
  "payload": {
    "operation": "publish-news",
    "newsId": "<NEWS_ID>",
    "scheduleId": "<SCHEDULE_ID>"
  }
}
```

Omitting `type` selects `general`. A future job is `Queued` and becomes eligible for acquisition at `scheduledAtUtc`. TaskForge compares submitted payload JSON text for idempotency. Keep the schedule instant and execution options stable as well. The key is case-sensitive, application-scoped, and at most 100 characters after trimming. News content stays in WoWiki.

## HTTP worker loop

| Step | Use |
| --- | --- |
| Acquire | `POST /api/executions/wait` with `applicationId`, a unique `workerId` per instance and slot, `supportedTypes: ["general"]`, and `waitSeconds: 20`. Set the HTTP timeout above the wait. Handle `204` as no work and parse a `200` as one assignment. Keep one outstanding wait per free slot. |
| Execute | Validate the returned application, owner, and payload. Keep the assignment's `jobId`, `attemptId`, and exact `workerId`. Dispatch the operation and stop at `deadlineAtUtc`. During active work, poll `GET /api/jobs/{id}` for cancellation or a change in `status`, `owningWorkerId`, or `startedAtUtc`. Abort cooperatively. |
| Complete | `POST /api/jobs/{jobId}/attempts/{attemptId}/complete` with `applicationId` and the exact `workerId`. Omit the optional `result` unless the worker needs to record a small result. |
| Fail | `POST /api/jobs/{jobId}/attempts/{attemptId}/fail` with `applicationId`, the exact `workerId`, `errorCode`, and `errorMessage`. Use `InvalidPayload` for invalid input and `NonRetryableJobException` for an unknown operation, a superseded schedule, or a missed publication window when the attempt is still active. Use a retryable code for transient failures, such as `NewsStoreUnavailable`. |
| Cancel or inspect | `POST /api/jobs/{id}/cancel` takes an empty body. Read `GET /api/jobs/{id}` and `GET /api/jobs/{id}/attempts` when state or delivery is uncertain. An accepted cancel can close a running attempt while the remote handler is still active. |

TaskForge has no heartbeat or lease renewal. Use the fixed attempt deadline, which starts at acquisition. The default attempt timeout is 30 seconds. Lease grace is for server recovery. Abort idle waits when NestJS shuts down, and stop acquiring before draining active work.

## Atomic publication and recovery

- At the final WoWiki database write, require the expected scheduled state, `scheduleId`, and `taskForgeJobId` in one conditional update or transaction. Read the current News and its schedule from WoWiki. The assignment does not include `scheduledAtUtc`. Only the winning write may publish.
- Enforce the optional 30-minute publication window at that same write. If an unpublished item is beyond the window, skip publication and report the permanent code above. Ordinary scheduled items can publish late after downtime. If the same schedule already published and its report was lost, acknowledge success without publishing again.
- If the worker claims the current schedule before WoWiki attaches the job ID, defer via retryable failure. A stale schedule must never publish. Cancellation status polling helps stop work early. The database condition decides which action wins a race with Publish Now, rescheduling, or unscheduling.
- A lost acquisition response may already have created an attempt. Resume polling with backoff and let TaskForge recover the unseen attempt at timeout. After a transport failure, `408`, `429`, or `5xx` on a report, retry the same report within the attempt deadline and leave the handler result intact. An identical accepted report returns `200`. Stop retries on `400`, `404`, or `409`. An application credential error requires fixing configuration.
- Automatic execution retries keep the same job ID and create new attempt IDs. Do not use an attempt ID as a publication deduplication key. Permanent failure immediately dead-letters the job. A job can also be dead-lettered when retryable failures exhaust its budget. WoWiki may still show the News as scheduled until an editor action or later reconciliation.

## Minimal implementation order

1. Reuse the existing News publication service and state. Add the scheduling fields only where they are missing and implement the guarded database transition.
2. Add the HTTP client and editor scheduling actions. Surface definitive and uncertain submission outcomes separately. Keep the first version's recovery manual and key-based.
3. Wire the NestJS worker into startup and shutdown. Add the `publish-news` handler and enable the worker once the application key is provisioned.
4. Verify end to end against TaskForge and SQL Server. Cover due-time acquisition, ambiguous submission with one resulting job, a crash after publication before reporting, a race with Publish Now or rescheduling, and late publication with the deadline flag both on and off. Include Berlin clock transitions in the scheduling validation tests.

TaskForge routes by application and type. All WoWiki workers advertising `general` must understand each operation published to that type. Keep the initial integration within the existing News module, HTTP client, and worker loop.

## Verified contract

- [TaskForge HTTP API](https://github.com/Arvence/TaskForge/blob/ed856335bb9bd85523871da343b25d3e8b049e5b/docs/http-api.md)
- [Authentication and payload validation](https://github.com/Arvence/TaskForge/blob/ed856335bb9bd85523871da343b25d3e8b049e5b/docs/payload-contracts.md)
- [Execution ownership, deadlines, and retries](https://github.com/Arvence/TaskForge/blob/ed856335bb9bd85523871da343b25d3e8b049e5b/docs/architecture.md#ownership-deadlines-and-retries)
