# Changelog

## 1.0.0 - 2026-07-28

- Added durable SQLite-backed job submission and status APIs.
- Added an in-process, dynamically scalable `WorkerManager`.
- Added priority ordering, retries, timeouts, cancellation, and lease recovery.
- Added `delay` and allowlisted `http-request` job handlers.
- Added idempotent submission through the `Idempotency-Key` header.
- Added persisted worker-count control and an OpenAPI document.
