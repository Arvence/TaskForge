# Application access and payload contracts

TaskForge stores a job's validated inputs for later execution. Keep full application records in their source system. Submit a record reference, a versioned reference when a particular revision matters, or the small input values the handler needs. Job payloads, results, errors, and idempotency keys must not contain secrets.

## Configure an application

The server operator sets the API key and the available job contracts in server configuration. An application can use only the job types the operator assigns to it. There is no registration endpoint and no unrestricted fallback.

For local Docker Compose, create `.taskforge-apps.env` beside `compose.yaml`. The file is ignored by Git. For a standalone API, set the same keys through its environment or deployment secret provider. Restart the API after changing the configuration.

```dotenv
JobAccess__Applications__a-project__ApiKey=<SAMPLE_APPLICATION_API_KEY>
JobAccess__Applications__a-project__Jobs__generate-report__Contract=generate-report
JobAccess__Applications__a-project__Jobs__http-request__Contract=http-get
JobAccess__Applications__a-project__Jobs__http-request__Target=http://localhost:8275
```

Replace the placeholder with a unique, randomly generated secret. Keys must be 32–128 characters using ASCII letters, digits, hyphens, or underscores. For example, generate 32 random bytes and encode them as hex. Keep keys outside source control and use HTTPS when crossing hosts. The default Compose port binds to host loopback.

Send `Authorization: Bearer <APPLICATION_API_KEY>` for job submission, acquisition, outcome reporting, job inspection and management, and statistics. The same key can be used by the submitter and worker for that application. The .NET SDK accepts `TaskForgeClientOptions.ApiKey`, and the sample CLI and debugging console read `TaskForge__ApiKey`. Health, readiness, and OpenAPI remain accessible without a key.

An invalid or missing key returns `401`. A request claiming another valid application ID returns `403`. Job lookup or management by ID returns `404` for another application's jobs. Lists and statistics are scoped to the authenticated application. A valid application ID alone never grants access.

## Supported profiles

Register a job type under `JobAccess:Applications:<applicationId>:Jobs:<type>:Contract`. The registered job type may differ from the profile name. Profile names and JSON fields are case-sensitive. The job type used for registration lookup is case-insensitive.

| Profile | Required payload fields | Optional fields and limits |
| --- | --- | --- |
| `generate-report` | `title` and `entries`. Each entry has `category` and decimal `amount`. | Title up to 120 characters, 1–1,000 entries, category up to 80 characters, amount from 0 to 1 trillion with at most two decimal places. |
| `calculation` | `values`, an array of 1–1,000 decimal numbers. | No optional fields. The application supplies the matching calculation handler. |
| `http-get` | `url` and `method: "GET"`. | `body` and `headers` may be omitted or null. The URL must match the configured `Target` origin and cannot contain credentials, a query, or a fragment. |
| `wowiki-report` | `url`, `method: "POST"`, `body.reportId`, `body.externalReference`, and `headers.X-TaskForge-Source`. | No optional fields. The current WoWiki receiver requires the external reference, so TaskForge requires it too. |

For the WoWiki `http-request` job, set the profile to `wowiki-report` and `Target` to the private WoWiki base URL the worker can reach. The URL must be that base followed by `/api/internal/taskforge/user-reports/{reportId}/ready`. A report ID is a positive numeric string, its reference must be `WOW-{reportId}`, and the source marker must be `wowiki-user-reports`. Report content and credentials stay out of the payload. The source marker is not a credential. The worker's own HTTP host allowlist still applies. The WoWiki backend must supply its application ID and matching bearer key on submission.

```dotenv
JobAccess__Applications__wowiki__ApiKey=<WOWIKI_APPLICATION_API_KEY>
JobAccess__Applications__wowiki__Jobs__http-request__Contract=wowiki-report
JobAccess__Applications__wowiki__Jobs__http-request__Target=<PRIVATE_WOWIKI_BASE_URL>
```

Add a new payload shape through a reviewed validator in the server's [payload policy](../src/TaskForge.Api/Jobs/JobPayloadPolicy.cs) and register its profile for the applications that need it. Application clients and SDK handler registration cannot change the server policy.

## Validation and existing jobs

The API limits payloads to 16 KiB in UTF-8 and limits the submit request body to 32 KiB when served by Kestrel. An unregistered application/job pair, missing field, unknown or duplicate field, invalid type, or invalid value returns a validation error before any job write. The validator checks nested objects and arrays. It preserves the original payload text for existing idempotency comparisons.

Previously stored jobs continue through the existing acquisition, retry, and outcome paths. This change does not rewrite historical data. Manual replay creates a new job, so it validates the payload against the current contract and rejects a legacy shape that no longer passes. Coordinate contract and key configuration before upgrading clients.

The allowlist controls the structure and bounds of job inputs. It cannot tell whether a permitted title contains personal data. Senders and workers still need to keep sensitive data out of inputs, results, and error messages. Existing job retention is unchanged. Set retention and deletion requirements for your deployment before processing data that needs a defined lifetime.
