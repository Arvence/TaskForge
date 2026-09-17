# TaskForge

TaskForge is a .NET 8 background job-processing system backed by Microsoft SQL
Server (MSSQL). It lets applications submit work over HTTP, process it
asynchronously, and inspect progress and execution history. The current job
handler sends HTTP requests to allowlisted hosts.

## Key Features

- Persistent jobs and worker settings using EF Core and MSSQL.
- Priority-based processing with up to eight workers, adjustable at runtime.
- Timeouts, capped exponential retries, dead-letter handling, and cancellation.
- Idempotent submission, optimistic concurrency, and expired-lease recovery.
- Filtered and paginated job lists, execution attempt history, and job statistics.
- Docker Compose setup, automated tests, and GitHub Actions CI.

## Architecture / Request Flow

```mermaid
---
config:
  flowchart:
    curve: linear
---
flowchart LR
    Client --> API["TaskForge.Api"]
    API --> App["TaskForge.Application"]
    App -->|Persistence interfaces| Infra["TaskForge.Infrastructure"]
    Infra --> DB[("MSSQL")]
    App -.->|State transitions| Domain["TaskForge.Domain"]
```

- **Api:** HTTP endpoints, dependency registration, and hosting for the workers.
- **Application:** submission validation, job workflows, and worker orchestration;
  depends on persistence interfaces rather than EF Core.
- **Domain:** job states, attempt outcomes, and lifecycle rules.
- **Infrastructure:** EF Core persistence, SQL Server migrations, and the HTTP handler.

A submission is validated, queued, and saved before the API returns its ID.
Workers in the same process acquire eligible jobs from MSSQL, execute the
handler, and persist the result and attempt history.

## Job Lifecycle

```mermaid
---
config:
  flowchart:
    curve: linear
---
flowchart LR
    Start((Start)) --> Pending
    Pending -->|Submit| Queued
    Queued -->|Acquire| Processing
    Processing -->|Success| Completed
    Processing -->|Retryable failure or timeout| Retrying
    Retrying -->|Retry due| Queued
    Processing -->|Permanent failure or retries exhausted| DeadLettered
    Processing -->|Lease expired| Queued
    Pending -->|Cancel| Cancelled
    Queued -->|Cancel| Cancelled
    Retrying -->|Cancel| Cancelled
    Processing -->|Cancellation confirmed| Cancelled
```

`Pending` is the initial domain state; accepted submissions are stored as
`Queued`. Retries wait for their backoff before becoming eligible again.
Lease recovery requeues interrupted work unless cancellation was requested,
in which case it becomes `Cancelled`. Cancelling a running job first sets a
cancellation request; the terminal state is persisted afterward.

## API Endpoints

| Method | Endpoint | Description |
| --- | --- | --- |
| `POST` | `/api/jobs` | Submit a job, optionally with an `Idempotency-Key` header. |
| `GET` | `/api/jobs` | Filter by status, type, or priority; paginate newest-first results. |
| `GET` | `/api/jobs/{id}` | Retrieve a job's state and result. |
| `GET` | `/api/jobs/{id}/attempts` | Retrieve attempts in ascending attempt-number order. |
| `POST` | `/api/jobs/{id}/cancel` | Request cancellation of an unfinished job. |
| `GET` | `/api/workers` | Inspect workers and the desired worker count. |
| `PUT` | `/api/workers/count` | Set the worker count using `{"count": 4}`; accepts `0`–`8`. |
| `GET` | `/api/stats` | Get current job counts and success rate. |
| `GET` | `/api/health` | Check application liveness without querying the database. |
| `GET` | `/api/ready` | Check database connectivity; returns `200` or `503`. |
| `GET` | `/openapi/v1.json` | Read the generated OpenAPI document. |
| `GET` | `/` | Redirect to `/api/health`. |

Job lists return `items`, `page`, `pageSize`, `totalCount`, and `totalPages`.
Pages start at `1`; page size defaults to `50` and accepts `1`–`100`.
For example: `GET /api/jobs?status=Queued&priority=High&page=1&pageSize=25`.

## Request / Response Examples

Use `http://localhost:8275` as the base URL and replace `{id}` with a returned
job ID. Job response bodies below show selected fields; the examples illustrate
different outcomes rather than a single sequence.

### Submit a job

`POST /api/jobs` — `Content-Type: application/json`,
`Idempotency-Key: health-check-001`

Request:

```json
{
  "type": "http-request",
  "priority": "High",
  "payload": {
    "url": "http://localhost:8080/api/health",
    "method": "GET"
  },
  "maxRetries": 3,
  "timeoutSeconds": 30
}
```

Response: `201 Created`, with a `Location` header pointing to the job.

```json
{
  "id": "a70c8b73-6bb2-4fa4-a2bb-4f5c054c7a72",
  "type": "http-request",
  "status": "Queued"
}
```

The job calls port `8080` inside the API container; the client submits through
host port `8275`. Replaying the same submission and idempotency key returns
`200` with the original job; changing the submission under that key returns `409`.

### Retrieve a job

`GET /api/jobs/{id}` — example `200 OK` response after execution:

```json
{
  "id": "a70c8b73-6bb2-4fa4-a2bb-4f5c054c7a72",
  "status": "Completed",
  "retryCount": 0,
  "result": { "statusCode": 200, "reasonPhrase": "OK" }
}
```

### Cancel a job

`POST /api/jobs/{id}/cancel` — no request body.
Example `200 OK` response for a job cancelled while queued:

```json
{
  "id": "a70c8b73-6bb2-4fa4-a2bb-4f5c054c7a72",
  "status": "Cancelled",
  "cancellationRequested": true
}
```

An unknown job returns `404`; an already finished job or a conflicting update
returns `409`.

### Inspect execution attempts

`GET /api/jobs/{id}/attempts` — example `200 OK` response:

```json
[
  {
    "jobId": "a70c8b73-6bb2-4fa4-a2bb-4f5c054c7a72",
    "attemptNumber": 1,
    "workerId": "taskforge-api-1-1",
    "startedAtUtc": "2026-09-17T12:00:00Z",
    "finishedAtUtc": "2026-09-17T12:00:00.250Z",
    "durationMilliseconds": 250,
    "outcome": "Succeeded",
    "errorCode": null,
    "errorMessage": null
  }
]
```

Outcomes are `Running`, `Succeeded`, `Failed`, `PermanentlyFailed`, `TimedOut`,
`Cancelled`, or `Abandoned`. Running attempts have no finish time or duration.
A job with no executions returns `[]`; a missing job returns `404`.

### Get job statistics

`GET /api/stats` — example `200 OK` response:

```json
{
  "totalJobs": 10,
  "countsByStatus": {
    "pending": 0,
    "queued": 1,
    "processing": 1,
    "retrying": 0,
    "completed": 6,
    "deadLettered": 2,
    "cancelled": 0
  },
  "successRatePercent": 75
}
```

Statistics describe current job states, not execution attempts. Success rate is
`completed / (completed + deadLettered) * 100`, rounded to two decimal places;
it is `null` when neither outcome exists.

## Quick Start

Requires Docker with Compose v2; on Windows, use Linux container mode.
From the repository root, create `.env` on first setup (PowerShell):

```powershell
Copy-Item .env.example .env
```

Replace the `MSSQL_SA_PASSWORD` placeholder with a strong, unique password.
Keep an existing `.env` if already configured; it is excluded from Git.

```powershell
docker compose config --quiet
docker compose up --build -d
```

Compose starts MSSQL and the API, applies migrations, and preserves database
data in a named volume. Access the [health endpoint](http://localhost:8275/api/health)
or [OpenAPI JSON](http://localhost:8275/openapi/v1.json).
`docker compose down` stops the stack while retaining data.

Outbound HTTP hosts must be explicitly allowed in
[appsettings.json](src/TaskForge.Api/appsettings.json) or environment overrides
in [compose.yaml](compose.yaml). Defaults are `localhost` and `127.0.0.1`;
redirects are disabled.

## Testing & CI

Use the .NET 8 SDK for local development. Unit tests run without Docker;
integration tests use real MSSQL through Testcontainers and require a running
Linux container engine.

```powershell
dotnet test tests/TaskForge.UnitTests --configuration Release
dotnet test TaskForge.sln --configuration Release
dotnet build TaskForge.sln --configuration Release
```

The [GitHub Actions workflow](.github/workflows/ci.yml) runs on pushes and pull
requests to `main`, or manually. Windows checks the Release build and unit
tests; Ubuntu runs MSSQL integration tests and builds the API Docker image.
The workflow does not publish or deploy the image.

## Known Limitations

- Designed for one API instance with in-process workers on a trusted network;
  the API has no authentication.
- External requests can repeat after retries or lease recovery. Submission
  idempotency does not guarantee exactly-once side effects; receivers must
  tolerate duplicate calls.
- Only registered handlers execute; `http-request` is the currently supported
  job type. Job payloads and headers are readable through the API and should
  not contain secrets.
- Attempt history covers new executions only. For expired leases, the recorded
  finish time is when abandonment was detected, not the exact interruption time.
