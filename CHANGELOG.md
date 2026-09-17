# Changelog

Notable changes to TaskForge are recorded here. Unreleased changes follow the
SQLite-based `v1.0.0` release and are not included in that tag.

## Unreleased

### Added

- Execution attempt recording with worker identifiers, timing, duration,
  outcomes, and bounded error details in the existing `JobAttempts` table.
- `GET /api/jobs/{id}/attempts` with ascending attempt-number ordering,
  empty histories for unexecuted jobs, and `404` for missing jobs.
- Attempt-history tests covering successful execution, retries, permanent
  failures, timeouts, cancellation, worker shutdown, lease recovery, concurrent
  starts, and API retrieval.
- Job statistics at `GET /api/stats`, including current status counts and
  success rate.
- Database readiness at `GET /api/ready`, returning `200` or `503` without
  exposing connection or exception details.
- Centralized Problem Details responses for unexpected API exceptions,
  with server-side logging.
- SQL Server migrations applied automatically during API startup.
- A multi-stage .NET 8 Docker image and Compose environment with SQL Server
  2022 Developer, database health checks, persistent storage, and API restarts.
- An example environment file and exclusions for local secrets in Git and
  Docker builds.
- A debugging console for inspecting API health, workers, and filtered jobs.
- HTTP contract tests for submission, request validation, missing jobs,
  idempotency, statistics, cancellation, and readiness.
- SQL Server integration coverage for competing workers, optimistic
  concurrency, idempotency constraints, retries, lease recovery, persisted
  worker counts, and cancellation races.
- GitHub Actions CI with Windows build and unit-test checks, followed by
  Ubuntu SQL Server integration tests and Docker image validation.

### Changed

- Replaced SQLite with SQL Server, using native `datetimeoffset` mappings,
  case-insensitive job-type filtering, and unique idempotency keys.
- Made an explicit SQL Server connection string required at startup.
- Changed `GET /api/jobs` to return a filtered, paginated response with a
  bounded page size.
- Added registered job-type and handler payload validation before queueing.
- Refined retry handling with permanent-failure detection and capped
  exponential backoff.
- Changed the default API port to `8275`.
- Moved database-dependent tests into `TaskForge.IntegrationTests`, using a
  shared Testcontainers SQL Server instance and an isolated, migrated database
  per test. Unit tests remain independent of Docker.
- Expanded documentation for setup, configuration, HTTP integrations,
  testing, and operating limits.

### Removed

- The demonstration `delay` job handler.

### Upgrade notes

This update is not a drop-in replacement for the SQLite release:

- Configure SQL Server and provide `ConnectionStrings__TaskForge`. Startup
  migrations create the SQL Server schema but do not transfer SQLite data.
- Update job-list consumers to read the paginated response's `items` and
  pagination metadata.
- Replace submissions that use the removed `delay` handler.
- Update clients to use port `8275` when relying on the default configuration.

The service continues to target one API instance on a trusted network.
Authentication is not implemented. Attempt history covers new executions only;
existing executions are not backfilled. External
requests may execute more than once; receivers must tolerate duplicate
callbacks. See the [known limitations](README.md#known-limitations) for details.

## 1.0.0 - 2026-07-28

- Added durable SQLite-backed job submission and status APIs.
- Added an in-process, dynamically scalable `WorkerManager`.
- Added priority ordering, retries, timeouts, cancellation, and lease recovery.
- Added `delay` and allowlisted `http-request` job handlers.
- Added idempotent submission through the `Idempotency-Key` header.
- Added persisted worker-count control and an OpenAPI document.
