# Changelog

## Unreleased

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
