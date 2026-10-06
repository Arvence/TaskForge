# HTTP API

[README](../README.md) · [Architecture](architecture.md) · [.NET client](dotnet-client.md)

The default local base URL is `http://localhost:8275`. Send JSON bodies with
`Content-Type: application/json`. Responses use camelCase properties and string
enum names. The running server exposes its schema at
[/openapi/v1.json](http://localhost:8275/openapi/v1.json); import that URL into
Postman for request and response schemas. There is no bundled Swagger UI.

All operations are available without the SDK. A Node.js or Python integration
needs an HTTP client and a worker loop: acquire work, execute a local function,
then report the outcome. No inbound worker endpoint is needed. In a web app,
submission and status reads belong in the backend; execution belongs in a running
backend worker, not a browser tab.

## Endpoints

| Method | Path | Behavior |
| --- | --- | --- |
| `POST` | `/api/jobs` | Create a job (`201`) or return an identical idempotent submission (`200`). |
| `GET` | `/api/jobs` | Filter and page jobs (`200`). |
| `GET` | `/api/jobs/{id}` | Read job status, payload, result, and last error (`200`, `404`). |
| `GET` | `/api/jobs/{id}/attempts` | Read attempts in ascending attempt-number order (`200`, `404`). |
| `POST` | `/api/jobs/{id}/cancel` | Cancel a job (`200`, `404`, `409`). |
| `POST` | `/api/jobs/{id}/retry` | Replay a canceled or dead-lettered job as a new job (`201`, `404`, `409`). |
| `POST` | `/api/executions/wait` | Acquire one compatible assignment (`200`) or return no work (`204`). |
| `POST` | `/api/jobs/{jobId}/attempts/{attemptId}/complete` | Report success (`200`, `404`, `409`). |
| `POST` | `/api/jobs/{jobId}/attempts/{attemptId}/fail` | Report failure (`200`, `404`, `409`). |
| `GET` | `/api/health` | API liveness (`200`). |
| `GET` | `/api/ready` | Database connectivity (`200`, `503`). |
| `GET` | `/api/stats` | Global current-state counts and success rate (`200`). |

Requests with invalid fields return `400`; unsupported request content types return
`415`. Errors may be Problem Details or endpoint-specific JSON; use the HTTP status
and schema rather than assuming every error has the same shape. The compatibility
routes `GET /api/workers` and `PUT /api/workers/count` return `410`; configure worker
capacity in clients.

## Submit work

Send this body to `POST /api/jobs` while the sample worker runs for `a-project`:

```json
{
  "applicationId": "a-project",
  "type": "generate-report",
  "payload": {
    "title": "Expenses",
    "entries": [
      { "category": "Travel", "amount": 125.50 },
      { "category": "Supplies", "amount": 40.25 }
    ]
  },
  "priority": "Normal",
  "maxRetries": 3,
  "timeoutSeconds": 30
}
```

The response's `id` is the job ID. A successful submission means the job is stored;
it does not mean a handler has run.

| Field | Contract |
| --- | --- |
| `applicationId` | Required; 1–100 ASCII letters, digits, dots, underscores, or hyphens after trimming. Stored lowercase. |
| `type` | Required nonblank name, at most 100 characters after trimming. Routing is case-insensitive. |
| `payload` | Required non-null JSON value. The client validates its business meaning. |
| `priority` | `Low`, `Normal`, `High`, or `Critical`; defaults to `Normal`. |
| `maxRetries` | 0–10 automatic retries after the initial attempt; defaults to 3. |
| `timeoutSeconds` | 1–3,600 seconds per attempt, starting at acquisition; defaults to 30. |

An optional `Idempotency-Key` header accepts 1–100 nonblank characters after
trimming. Keys are case-sensitive and scoped to an application. Reusing a key
with the same submission returns the existing job; different content returns
`409`. Keep the original request body when retrying submission: payload comparison
uses stored JSON text, not semantic JSON equivalence.

## Acquire and report

Send a wait request from each available worker slot:

```json
{
  "applicationId": "a-project",
  "workerId": "report-worker-1",
  "supportedTypes": ["generate-report"],
  "waitSeconds": 20
}
```

`workerId` must be nonblank and at most 200 characters. Use a distinct ID for each
active worker slot. `supportedTypes` accepts 1–100 nonblank names, each at most 100
characters. `waitSeconds` is 0–30 and defaults to 20; zero performs a single poll.
Set the HTTP timeout longer than the requested wait.

A `200` assignment contains `jobId`, `applicationId`, `attemptId`, `attemptNumber`,
`workerId`, `type`, `payload`, `timeoutSeconds`, `startedAtUtc`, `deadlineAtUtc`, and
`leaseExpiresAtUtc`. Preserve this identity. The deadline includes time spent
delivering the assignment to the client.

After local execution, post to
`/api/jobs/{jobId}/attempts/{attemptId}/complete`:

```json
{
  "applicationId": "a-project",
  "workerId": "report-worker-1",
  "result": { "totalAmount": 165.75 }
}
```

`result` can be omitted or null; otherwise its serialized JSON must fit within
4,000 characters. For a business failure, post to the corresponding `/fail` path:

```json
{
  "applicationId": "a-project",
  "workerId": "report-worker-1",
  "errorCode": "RemoteServiceUnavailable",
  "errorMessage": "The reporting service could not be reached."
}
```

Error codes accept 1–100 characters and messages 1–4,000 after trimming. Codes are
normalized to lowercase. `InvalidPayload`, `UnsupportedJobType`, and
`NonRetryableJobException` cause permanent failure; other codes follow the server's
retry budget and backoff. Clients cannot supply their own next retry time.

### Uncertain delivery

- `200` acknowledges an outcome, including an identical previously accepted report.
  Repeated failure reports do not consume additional retries. The returned job
  can reflect a later attempt when acknowledging a historical failure report.
- On connection failures, timeouts, `408`, `429`, or `5xx`, retry the **same outcome
  and identity** with bounded backoff. Do not rerun the handler or report a business
  failure because delivery failed.
- Stop reporting on `400`, `404`, or `409`. A `409` may indicate a stale, canceled,
  timed-out, or conflicting outcome. Deadline rejection includes
  `code: "AttemptTimedOut"`.
- Do not retry outcome delivery indefinitely past the execution deadline. Without
  an accepted outcome, server recovery determines whether another attempt is due.
- Acquisition is not idempotent. Do not add transparent HTTP retries to wait
  requests: a lost response may already have assigned work. A subsequent wait can
  acquire a different job; an unseen assignment must recover through its timeout.

During execution, a custom worker should inspect the job for cancellation or loss
of ownership and honor the assignment deadline. See [execution semantics](architecture.md#ownership-deadlines-and-retries).

## Inspect, cancel, and replay

In Postman, set the method to `GET`, leave the body empty, and replace `{id}` with
the submission response's job ID:

- `/api/jobs/{id}` returns `status`, `result`, `retryCount`, `lastError`, ownership,
  and timestamps alongside the original submission.
- `/api/jobs/{id}/attempts` returns an array with attempt identity, worker, timing,
  outcome, and errors. An existing job with no attempts returns `[]`.
- `/api/jobs?applicationId=a-project&status=Completed&page=1&pageSize=20` returns
  `items`, `page`, `pageSize`, `totalCount`, and `totalPages`, newest first. Filters
  also include `type` and `priority`. Page size defaults to 50 and allows 1–100.

Cancellation and replay use `POST` with no body. Repeating cancellation of an
already canceled job returns `200`. Completed and dead-lettered jobs cannot be
canceled. Replay preserves application, type, payload, priority, retry limit, and
timeout, but creates a new ID with no idempotency key. Every successful replay
request creates another job; the `Idempotency-Key` header is ignored on this route.

Job reads and management routes are not access-controlled by application ID.
Keep the API on a trusted network. Job data remains readable while workers are offline.

## Plain HTTP example

With the server running, execute this entire block in PowerShell. It needs no .NET
SDK or sample worker: PowerShell itself acquires the job, computes the sum, and
reports it. A unique application ID keeps this example separate from other clients.

```powershell
$ErrorActionPreference = 'Stop'
$api = 'http://localhost:8275'
$applicationId = 'http-example-' + [Guid]::NewGuid().ToString('N')
$submission = @{
    applicationId = $applicationId
    type = 'calculate-total'
    payload = @{ values = @(1, 2, 3) }
    timeoutSeconds = 120
} | ConvertTo-Json -Depth 5
$job = Invoke-RestMethod "$api/api/jobs" -Method Post -ContentType 'application/json' -Body $submission

$wait = @{
    applicationId = $applicationId
    workerId = 'powershell-worker'
    supportedTypes = @('calculate-total')
    waitSeconds = 0
} | ConvertTo-Json
$assignment = Invoke-RestMethod "$api/api/executions/wait" -Method Post -ContentType 'application/json' -Body $wait
if ($null -eq $assignment -or $assignment.jobId -ne $job.id) { throw 'Expected the submitted job.' }

$total = ($assignment.payload.values | Measure-Object -Sum).Sum
$report = @{
    applicationId = $applicationId
    workerId = $assignment.workerId
    result = @{ total = $total }
} | ConvertTo-Json -Depth 5
$reportUrl = "$api/api/jobs/$($assignment.jobId)/attempts/$($assignment.attemptId)/complete"
$completed = Invoke-RestMethod $reportUrl -Method Post -ContentType 'application/json' -Body $report
if ($completed.status -ne 'Completed' -or $completed.result.total -ne 6) { throw 'Unexpected result.' }

$completed | Format-List id, status, result, retryCount
$attempts = Invoke-RestMethod "$api/api/jobs/$($job.id)/attempts"
$attempts | Format-Table attemptNumber, outcome, workerId, durationMilliseconds -AutoSize
```

Expect `Completed`, a total of `6`, and one `Succeeded` attempt. This single-job
example demonstrates the protocol; a continuously running client also needs the
delivery, cancellation, and deadline handling described above.
