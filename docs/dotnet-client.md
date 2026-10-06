# .NET client

[README](../README.md) · [Architecture](architecture.md) · [HTTP API](http-api.md)

`TaskForge.SDK` targets .NET 10 and has no server project references. It provides
three pieces that can be used together or separately:

| Component | Purpose |
| --- | --- |
| `TaskForgeClient` | Typed HTTP submission, acquisition, reporting, job reads, attempt history, and cancellation. |
| `JobHandlerRegistry` | Local type-to-handler mappings and a separate dependency-injection scope per invocation. |
| `TaskForgeWorker` | Hosted execution slots, cancellation monitoring, deadlines, and bounded outcome-delivery retries. |

Reference the SDK project from this repository. Console commands belong to the
sample application; the SDK does not install a CLI.

## Register a worker

From the repository root, create a standalone console project and add its dependencies:

```powershell
dotnet new console --name ExampleWorker --output artifacts/ExampleWorker --framework net10.0
dotnet add artifacts/ExampleWorker reference src/TaskForge.SDK/TaskForge.SDK.csproj
dotnet add artifacts/ExampleWorker package Microsoft.Extensions.Hosting --version 10.0.12
```

Replace `artifacts/ExampleWorker/Program.cs` with the following. In an existing
hosted application, use the same registrations in its startup code.

```csharp
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using TaskForge.SDK;
using TaskForge.SDK.Execution;
using TaskForge.SDK.Handlers;

HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);
using HttpClient http = new() { Timeout = Timeout.InfiniteTimeSpan };
TaskForgeClient client = new(http, new()
{
    BaseUrl = new Uri("http://localhost:8275"),
    ApplicationId = "example-app"
});

builder.Services.AddSingleton(client);
builder.Services.AddTaskForgeHandlers(handlers => handlers.Register<SumPayload, SumHandler>("calculate-total"));
builder.Services.AddTaskForgeWorker(new() { SlotCount = 2 });

using IHost host = builder.Build();
await host.RunAsync();

public sealed record SumPayload(decimal[] Values);

public sealed class SumHandler(JobExecutionContext context) : IJobHandler<SumPayload>
{
    public Task<JsonElement?> HandleAsync(SumPayload payload, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (payload.Values.Length is < 1 or > 100)
        {
            throw new NonRetryableJobException("Provide between 1 and 100 values.");
        }

        JsonElement result = JsonSerializer.SerializeToElement(new { total = payload.Values.Sum(), jobId = context.JobId });
        return Task.FromResult<JsonElement?>(result);
    }
}
```

Run `dotnet run --project artifacts/ExampleWorker`. It accepts `calculate-total`
jobs for `example-app` with payloads such as `{"values":[1,2,3]}`. Job submission
belongs in your application, separately from worker startup.

Register all mappings in one `AddTaskForgeHandlers` call. Names are case-insensitive
and frozen after startup. Handlers use scoped or transient lifetimes, never
singletons. The registry deserializes typed payloads and creates an async scope
for each execution; handlers validate business rules.

Inject `JobExecutionContext` into a handler to access `JobId`, `ApplicationId`,
`AttemptId`, `AttemptNumber`, `WorkerId`, `JobType`, and execution timestamps.
It is available when the registry executes an assignment. An attempt ID changes
on retry; choose a stable business key when protecting external effects.

## Submit and inspect

In a submitting application, configure `TaskForgeClient` with the same base URL
and application ID as the worker. This complete console example submits one job
and reads its current state; the worker must already be running to execute it.

```csharp
using System.Text.Json;
using TaskForge.SDK;

using HttpClient http = new() { Timeout = Timeout.InfiniteTimeSpan };
TaskForgeClient client = new(http, new()
{
    BaseUrl = new Uri("http://localhost:8275"),
    ApplicationId = "example-app"
});

var payload = JsonSerializer.SerializeToElement(new { values = new[] { 1, 2, 3 } });
var submitted = await client.SubmitAsync(new("calculate-total", payload));
var current = await client.GetJobAsync(submitted.Id);
var attempts = await client.GetAttemptsAsync(submitted.Id);
Console.WriteLine($"Job {current.Id}: {current.Status}; attempts: {attempts.Count}");
```

An immediate read may still show `Queued` or `Processing`. Poll `GetJobAsync` with
a bounded wait to observe completion. `CancelAsync` requests cancellation.
For submission retries, pass a stable `idempotencyKey` to `SubmitAsync` and retain
the original request. The SDK also exposes `WaitAsync`, `CompleteAsync`, and
`FailAsync` for applications that manage their own execution loop. Listing,
statistics, and manual replay use the [HTTP API](http-api.md#endpoints).

`TaskForgeClient` does not retry individual HTTP calls automatically. It throws
`TaskForgeApiException` for error responses, exposing `StatusCode`, `Error`, and
`ResponseBody`. Request timeouts throw `TimeoutException`; caller cancellation
remains cancellation. Its default request timeout is 30 seconds; waits allow at
least `waitSeconds + 10`. Use an infinite `HttpClient.Timeout` as above so the SDK
can apply the appropriate per-request timeout.

## Worker behavior

| `TaskForgeWorkerOptions` | Default | Meaning |
| --- | --- | --- |
| `SlotCount` | `1` | Concurrent executions; each slot has its own worker identity. |
| `WaitSeconds` | `20` | Server wait duration, from 0 to 30 seconds. |
| `NoWorkDelay` | `250 ms` | Delay after a wait returns no assignment. |
| `TransportErrorDelay` | `1 s` | Delay after transient communication failures. |
| `ReportRetryCount` | `3` | Additional delivery attempts for the same outcome. |
| `StatusPollInterval` | `1 s` | Check for cancellation or changed ownership during execution. |
| `ShutdownTimeout` | `10 s` | Maximum time the worker waits during shutdown. |

The worker advertises registered types and invokes each handler once per acquired
assignment. It passes a cancellation token linked to shutdown, the deadline, and
observed server cancellation or ownership changes. A slot stays occupied until
its handler exits, even if the handler ignores cancellation.

Handler exceptions produce failure reports. `NonRetryableJobException`, invalid
typed payloads, and missing handler mappings lead to permanent failure. Other
failure codes follow server retry policy. Results must be serializable JSON and
fit the 4,000-character limit.

Transient reporting failures retry the original outcome and identity without
rerunning the handler. Retry delivery ends at its bound or deadline; `404` and
`409` stop reporting for that assignment. Other permanent protocol errors, such
as `400`, fault the worker rather than being retried. Reports are held only in
memory; after process loss, the server recovers unreported work. Transport backoff
does not set a job's retry schedule or consume its retry budget.

## Sample client

After the [Release build](../README.md#build-and-test), run these commands from
the repository root. `run` stays active; each submission command prints a job ID
and exits, so use a second terminal for submission.

```powershell
dotnet run --project samples/TaskForge.SampleClient --configuration Release --no-build -- help
dotnet run --project samples/TaskForge.SampleClient --configuration Release --no-build -- run
```

```powershell
dotnet run --project samples/TaskForge.SampleClient --configuration Release --no-build -- submit-report
dotnet run --project samples/TaskForge.SampleClient --configuration Release --no-build -- submit-http http://localhost:8275/api/health
```

To submit your own report, save this as `report.json` in your working directory:

```json
{
  "title": "Office expenses",
  "entries": [
    { "category": "Supplies", "amount": 12.50 },
    { "category": "Travel", "amount": 25.00 }
  ]
}
```

```powershell
dotnet run --project samples/TaskForge.SampleClient --configuration Release --no-build -- submit-report report.json
```

Reports are deterministic: they group entries by category and calculate totals.
Titles allow 1–120 characters; reports accept 1–1,000 entries, nonblank categories
of at most 80 characters, and nonnegative amounts with up to two decimal places
and a maximum of 1,000,000,000,000. Reports exceeding the result limit are rejected.

The HTTP command submits a GET request. Its handler returns status code and reason
phrase, not the response body. The local allowlist defaults to `localhost` and
`127.0.0.1`; redirects are disabled. These handlers belong to the sample, not the
SDK or server.

The sample reads [clientsettings.json](../samples/TaskForge.SampleClient/clientsettings.json)
beside its executable, then environment overrides:

| Variable | Default |
| --- | --- |
| `TaskForge__BaseUrl` | `http://localhost:8275` |
| `TaskForge__ApplicationId` | `a-project` |
| `HttpRequestJobs__AllowedHosts__0` | `localhost` |
| `HttpRequestJobs__AllowedHosts__1` | `127.0.0.1` |

For another application, set `$env:TaskForge__ApplicationId = 'b-project'` in both
its worker and submission terminals. To add capacity for one application, start
another worker with the same application ID. Separate applications can use the
same type name with their own implementations.

## Debugging console

```powershell
dotnet run --project src/TaskForge.Debugging --configuration Release --no-build -- dashboard
dotnet run --project src/TaskForge.Debugging --configuration Release --no-build -- jobs --application-id a-project --status Completed
```

The console prints a snapshot and exits. `dashboard` shows health, global
statistics, and five recent jobs. `jobs` shows up to 20 and supports
`--application-id`, `--status`, `--type`, and `--priority`. Configure its base URL
in [debugsettings.json](../src/TaskForge.Debugging/debugsettings.json).
For a single job and its attempt history, use `GetJobAsync` / `GetAttemptsAsync`
or [Postman](http-api.md#inspect-cancel-and-replay); neither console has a job-ID
inspection command.
