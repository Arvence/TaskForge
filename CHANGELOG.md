# Changelog

## Unreleased

- Replaced SQLite persistence with SQL Server, including native `datetimeoffset`
  mappings, case-insensitive job-type filtering, and unique idempotency keys.
- Added EF Core SQL Server migrations applied automatically during API startup
  and required explicit connection-string configuration.
- Added a multi-stage .NET 8 API image and Docker Compose services for the API
  and SQL Server 2022 Developer, with database health checks, a persistent named
  volume, and an API restart policy.
- Added `.env.example` and excluded local secrets from Git and Docker builds.
- Moved persistence and worker database tests into `TaskForge.IntegrationTests`,
  sharing one Testcontainers SQL Server instance with a migrated database per
  test. Unit tests remain independent of Docker.
- Added SQL Server coverage for competing workers, optimistic concurrency,
  idempotency constraints, retry ordering, lease recovery, persisted worker
  counts, and cancellation racing with completion.
- Documented Compose setup, container networking, test commands, and data loss
  when deleting the database volume.
- Added job filtering and bounded pagination to `GET /api/jobs`.
- Removed the demonstration `delay` job handler.
- Added registered job-type and handler payload validation before queueing.
- Added permanent-failure detection and capped exponential backoff for retries.

## 1.0.0 - 2026-07-28

- Added durable SQLite-backed job submission and status APIs.
- Added an in-process, dynamically scalable `WorkerManager`.
- Added priority ordering, retries, timeouts, cancellation, and lease recovery.
- Added `delay` and allowlisted `http-request` job handlers.
- Added idempotent submission through the `Idempotency-Key` header.
- Added persisted worker-count control and an OpenAPI document.
