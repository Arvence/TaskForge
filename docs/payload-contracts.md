# Application access and payload validation

TaskForge stores job inputs for later execution. Keep full application records
in their source system. Pass a reference or the small input values the worker
needs. Keep secrets out of payloads, results, errors, and idempotency keys.

## Configure an application

Copy the [sample configuration](../.taskforge-apps.env.example) to
`.taskforge-apps.env` beside `compose.yaml`. Replace the placeholder with a unique
random API key. Keys must use 32–128 ASCII letters, digits, hyphens, or
underscores. For a standalone API, supply the same setting through its
environment or deployment configuration. Restart the API after changing keys.

```dotenv
JobAccess__Applications__example-app__ApiKey=<APPLICATION_API_KEY>
```

The application ID and key authorize job submission for that application.
Clients send `Authorization: Bearer <APPLICATION_API_KEY>` for submission,
acquisition, outcome reporting, inspection, management, and statistics. The .NET
SDK accepts `TaskForgeClientOptions.ApiKey`. The CLI and debugging console read
`TaskForge__ApiKey`. Use HTTPS when crossing hosts. The default Compose port
binds to host loopback.

An invalid or missing key returns `401`. Claiming another valid application ID
returns `403`. Looking up or managing another application's job by ID returns
`404`. Lists and statistics are scoped to the authenticated application. Health,
readiness, and OpenAPI are public.

## Submit a payload

Omit `type` to submit a `general` job, or supply a custom type. Workers advertise
the types they can acquire. A job stays queued until a compatible worker requests
it and the job is due.

The server requires a `payload` field and accepts any JSON value, including
explicit `null`. It rejects duplicate object keys at any depth. The parser limit
is 64 levels, including the outer request object when parsing an HTTP request.
The payload limit is 16 KiB in UTF-8 and the Kestrel submission-body limit is
32 KiB. Workers validate business inputs, operation selectors, and any
restrictions on external calls before execution.

Remove old `Jobs:<type>:Contract` settings from the API configuration when
upgrading. Make sure each worker enforces the input rules it needs before
switching the API. Existing jobs keep their payloads and attempts. Manual replay
validates the payload against the same infrastructure rules as a new submission.
TaskForge preserves the original payload text for idempotency comparisons.
