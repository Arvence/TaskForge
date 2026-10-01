# Changelog

Notable changes to TaskForge are recorded here. Unreleased changes follow the
SQLite-based `v1.0.0` release and are not included in that tag.

## Unreleased

### Added

- Independent server maintenance for execution deadlines, with bounded batches,
  SQL Server time, and atomic timeout/history updates before lease grace expires.
- Execution attempt recording with worker identifiers, timing, duration,
  outcomes, and bounded error details in the existing `JobAttempts` table.
- `GET /api/jobs/{id}/attempts` with ascending attempt-number ordering,
  empty histories for unexecuted jobs, and `404` for missing jobs.
- Attempt-history tests covering successful execution, retries, permanent
  failures, timeouts, cancellation, lease recovery, concurrent acquisition,
  and API retrieval.
- Job statistics at `GET /api/stats`, including current status counts and
  success rate.
- Database readiness at `GET /api/ready`, returning `200` or `503` without
  exposing connection or exception details.
- Centralized Problem Details responses for unexpected API exceptions,
  with server-side logging.
- SQL Server migrations applied automatically during API startup.
- A multi-stage .NET 10 Docker image and Compose environment with SQL Server
  2022 Developer, database health checks, persistent storage, and API restarts.
- An example environment file and exclusions for local secrets in Git and
  Docker builds.
- A read-only debugging console for inspecting API health, job statistics,
  and jobs filtered by application, status, type, and priority.
- HTTP contract tests for submission, request validation, missing jobs,
  idempotency, statistics, cancellation, and readiness.
- SQL Server integration coverage for competing execution clients, optimistic
  concurrency, idempotency constraints, retries, lease recovery, preservation
  of legacy worker data, and cancellation races.
- GitHub Actions CI with Windows build and unit-test checks, followed by
  Ubuntu SQL Server integration tests and Docker image validation.

### Changed

- Upgraded the server, debugging console, and tests to .NET 10, with EF Core
  and its command-line tool at 10.0.12 and Swashbuckle at 10.2.3. Local builds,
  CI, and Docker now select .NET 10 without major-version roll-forward.
- Replaced SQLite with SQL Server, using native `datetimeoffset` mappings,
  case-insensitive job-type filtering, and unique idempotency keys.
- Made an explicit SQL Server connection string required at startup.
- Changed `GET /api/jobs` to return a filtered, paginated response with a
  bounded page size.
- Moved business execution to external clients that acquire work through
  `POST /api/executions/wait` and report completion or failure. The server owns
  persistent state, retries, deadlines, cancellation, and lease recovery.
- Submission validates the envelope and JSON while accepting arbitrary valid
  job types. Business payload validation and handlers belong to execution clients.
- Retired `/api/workers` and `/api/workers/count`; both return `410 Gone`.
  `Worker:Count` and persisted worker counts no longer control execution capacity.
- Refined retry handling with permanent-failure detection and capped
  exponential backoff.
- Changed the default API port to `8275`.
- Moved database-dependent tests into `TaskForge.IntegrationTests`, using a
  shared Testcontainers SQL Server instance and an isolated, migrated database
  per test. Unit tests remain independent of Docker.
- Expanded documentation for setup, configuration, HTTP integrations,
  testing, and operating limits.

### Removed

- The in-process executor, worker manager, built-in business handlers, local
  cancellation callbacks, and obsolete execution persistence APIs. Historical
  worker tables and settings remain for database compatibility.

### Upgrade notes

This update is not a drop-in replacement for the SQLite release:

- Install a stable .NET 10 SDK for local development and run `dotnet tool restore`
  to update the repository's EF tool. Rebuild Docker images when upgrading.
- Configure SQL Server and provide `ConnectionStrings__TaskForge`. Startup
  migrations create the SQL Server schema but do not transfer SQLite data.
- Update job-list consumers to read the paginated response's `items` and
  pagination metadata.
- Include `applicationId` in submissions and execution requests. Idempotency
  keys are scoped to an application.
- Run external execution clients with implementations for the submitted job
  types. Starting TaskForge runs server maintenance but does not execute jobs.
- Update clients to use port `8275` when relying on the default configuration.

The service continues to target one API instance on a trusted network.
Authentication is not implemented. Cancellation records server state and does
not confirm that remote code has stopped. Legacy acquisitions without attempts
can receive a `LegacyLeaseRecovery` history entry during reconciliation; this
does not prove execution began. External side effects may repeat after retries
or lease recovery; receivers must tolerate duplicate calls. See the
[known limitations](README.md#known-limitations) for details.

## 1.0.0 - 2026-07-28

- Added durable SQLite-backed job submission and status APIs.
- Added an in-process, dynamically scalable `WorkerManager`.
- Added priority ordering, retries, timeouts, cancellation, and lease recovery.
- Added `delay` and allowlisted `http-request` job handlers.
- Added idempotent submission through the `Idempotency-Key` header.
- Added persisted worker-count control and an OpenAPI document.
