# Application access and payload contracts

TaskForge stores validated inputs for later execution. Keep full application
records in their source system. Pass a reference or the small input values the
worker needs. Keep secrets out of payloads, results, errors, and idempotency keys.

## Configure an application

Copy the [sample configuration](../.taskforge-apps.env.example) to
`.taskforge-apps.env` beside `compose.yaml`. It registers the sample worker's
`generate-report` and `http-request` job types. Replace the placeholder with a
unique random API key. Keys must use 32–128 ASCII letters, digits, hyphens, or
underscores. For a standalone API, supply the same settings through its
environment or deployment configuration. Restart the API after changing them.

The operator controls which contracts are available to each application.
Clients send `Authorization: Bearer <APPLICATION_API_KEY>` for submission,
acquisition, outcome reporting, inspection, management, and statistics. The .NET
SDK accepts `TaskForgeClientOptions.ApiKey`. The sample CLI and debugging console
read `TaskForge__ApiKey`. Use HTTPS when crossing hosts. The default Compose port
binds to host loopback.

An invalid or missing key returns `401`. Claiming another valid application ID
returns `403`. Looking up or managing another application's job by ID returns
`404`. Lists and statistics are scoped to the authenticated application. Health,
readiness, and OpenAPI are public.

## Declare a payload contract

Set `JobAccess:Applications:<applicationId>:Jobs:<type>:Contract` to a JSON
string with a `properties` object. In the Compose environment file, use one
single-quoted JSON value per line:

```dotenv
JobAccess__Applications__example-app__ApiKey=<APPLICATION_API_KEY>
JobAccess__Applications__example-app__Jobs__refresh-record__Contract='{"properties":{"referenceId":{"type":"string","required":true,"nonBlank":true,"maxLength":128}}}'
JobAccess__Applications__example-app__Jobs__calculate__Contract='{"properties":{"values":{"type":"array","required":true,"minItems":1,"maxItems":1000,"items":{"type":"number"}}}}'
```

The first accepts a payload such as `{"referenceId":"record-123"}`. The contract
defines the reference's type, length, and requiredness. The worker interprets it
and decides how to look up the record. The second accepts small inline inputs,
such as `{"values":[12,30]}`. Each job type has its own contract.

| Field type | Settings |
| --- | --- |
| `string` | Optional `minLength` and `maxLength` in UTF-16 code units, `nonBlank`, and exact `allowedValues`. |
| `number` | Decimal number with optional inclusive `minimum` and `maximum` and `maxDecimalPlaces`. |
| `boolean` | JSON `true` or `false`. |
| `object` | A `properties` object containing named field declarations. |
| `array` | An `items` rule and optional inclusive `minItems` and `maxItems`. |
| `null` | Explicit JSON `null`, useful for fields a serializer emits with no value. |

Set `required` on named fields that must be present. The default is optional.
Object field names are case-sensitive and must be declared. Duplicate fields
are rejected even inside array items. Job type lookup is case-insensitive.
An empty `properties` object allows only an empty JSON object. Invalid
declarations stop API startup.

## Validation and upgrades

The payload limit is 16 KiB in UTF-8 and the Kestrel submission-body limit is
32 KiB. An unregistered application/job pair or invalid payload returns a
`400` with a `Payload` validation error before any job write.

When upgrading from named profiles, replace each profile value with a JSON
declaration and remove its server-side `Target` setting. For the sample HTTP
worker, set `HttpRequestJobs__AllowedOrigin` to the former GET target's origin.
For application-owned HTTP callbacks, deploy worker checks for the destination,
path, and business identifiers before replacing the old API profile. A worker
can construct a callback from a submitted reference and trusted configuration.

The server validates structure before persistence. A well-formed URL outside
the worker's allowed origin can pass a string declaration, but the sample worker
rejects it permanently before sending. For a fixed URL, a contract can use
`allowedValues` to reject other destinations during submission as well.

Existing jobs keep their original payloads, attempts, and automatic retry
behavior. Manual replay and new submissions, including idempotent
resubmissions, use the current declaration. A tighter contract can reject a
previously accepted payload. TaskForge preserves the original payload text for
idempotency comparisons.
