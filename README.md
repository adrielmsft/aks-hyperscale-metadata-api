# Metadata API

A RESTful ASP.NET Core (.NET 10) API for managing **Application** and **Maintainer**
metadata and the many-to-many links between them. Data is stored in file-backed
SQLite through EF Core. See the [design document](aks-metadata-api-design.md) for
the full requirements, validation scheme, and rationale.

[![CI](https://github.com/adrielmsft/aks-hyperscale-metadata-api/actions/workflows/ci.yml/badge.svg)](https://github.com/adrielmsft/aks-hyperscale-metadata-api/actions/runs/39537604052)

## Contents

- [Quick start](#quick-start)
- [API reference](#api-reference)
- [Production features](#production-features)
- [Storage](#storage)
- [Testing and CI](#testing-and-ci)
- [Key design decisions](#key-design-decisions)

## Quick start

The only prerequisite is Git plus PowerShell, which is built into Windows; on
Linux/macOS install [PowerShell 7](https://learn.microsoft.com/powershell/scripting/install/installing-powershell).
No database service, cloud account, or committed secrets are needed.

From the repository root, run [quickstart.ps1](quickstart.ps1):

```powershell
# Windows
powershell -ExecutionPolicy Bypass -File .\quickstart.ps1

# Linux / macOS
pwsh ./quickstart.ps1
```

The script:

1. Uses the .NET SDK pinned in `global.json` (10.0.402 or a newer 10.0.4xx patch).
   If it isn't installed, the script installs it into the git-ignored `.dotnet/`
   folder using Microsoft's official `dotnet-install` script; the system-wide
   SDK is left untouched.
2. Restores the local `dotnet-ef` tool and NuGet packages and builds the solution.
3. Creates or updates the SQLite database (`src/MetadataApi/metadata.db`) by
   applying migrations.
4. Reuses `Authentication__ApiKey` if it is set; otherwise generates a random key
   for this session.
5. Starts the API at `http://localhost:5108` and prints the key and ready-to-paste
   example requests. Press Ctrl+C to stop.

Options: `-Test` runs every test suite instead of starting the API, `-SetupOnly`
installs, builds, and migrates without starting it, and `-Port <n>` uses a
different port. The script stops with a clear message if the port is already in use.

```powershell
powershell -ExecutionPolicy Bypass -File .\quickstart.ps1 -Test
```

### Manual setup

With the SDK already installed (key generation below requires PowerShell 7+):

```powershell
$env:Authentication__ApiKey = [Convert]::ToHexString([System.Security.Cryptography.RandomNumberGenerator]::GetBytes(32))
dotnet tool restore
dotnet build MetadataApi.slnx
dotnet ef database update --project src\MetadataApi
dotnet run --project src\MetadataApi --launch-profile http
```

Then, from another terminal with the same `Authentication__ApiKey`:

```powershell
$headers = @{ Authorization = "Bearer $env:Authentication__ApiKey" }
$app = Invoke-RestMethod -Method Post -Uri http://localhost:5108/api/v1/applications -Headers $headers -ContentType application/json -Body '{"title":"Microsoft Garage","version":"1.2.0"}'
Invoke-RestMethod -Uri "http://localhost:5108/api/v1/applications/$($app.id)" -Headers $headers
curl.exe --fail http://localhost:5108/health/ready
```

Run all tests manually with `dotnet tool restore` followed by
`dotnet test MetadataApi.slnx --configuration Release`.

[MetadataApi.http](src/MetadataApi/MetadataApi.http) contains ready-made successful
and failing requests; it reads the key from the `Authentication__ApiKey` environment
variable. On Linux/macOS, use `/` path separators and `curl` instead of `curl.exe`.

## API reference

The committed [OpenAPI reference](docs/openapi.json) covers all 14 API operations,
request/response schemas, Bearer authentication, pagination, and error responses.
It lives in `docs/` so reviewers can browse it without running the service; optionally
import the file into [Swagger Editor](https://editor.swagger.io/) for an interactive view.
In Development, the same document is served anonymously at `GET /openapi/v1.json`;
production exposure is unchanged. Anonymous health probes are excluded (see below).

The `OpenApiTests` snapshot test compares the live generated document to the committed
file on every normal test run, including CI. JSON object ordering and whitespace do not
affect comparison; the document uses relative server URLs for portability. No build-time
app startup, real credentials, or developer database are needed: the test factory uses
an isolated SQLite test database and a dummy key. After changing the API, regenerate
from the repository root in PowerShell, review the diff, and commit the updated file:

```powershell
$env:UPDATE_OPENAPI_SNAPSHOT = '1'
try {
    dotnet test tests\MetadataApi.Tests -c Release --filter FullyQualifiedName~OpenApiTests
} finally {
    Remove-Item Env:UPDATE_OPENAPI_SNAPSHOT
}
```

Regeneration is disabled when `CI` is set, so CI cannot silently accept a stale spec.
405 is documented as a routing-level error in the spec description, not as a response
of an otherwise supported operation; 415 applies only to JSON-body operations.

### Conventions

- Base path `/api/v1`; plural resource names; server-generated GUID IDs in paths.
- Every `/api/v1` request requires `Authorization: Bearer <api-key>`.
- JSON with exact camelCase property names. Optional fields are returned as explicit
  `null`. Unknown properties (including `id`), duplicate properties, and wrong value
  types are rejected.
- `POST` returns `201 Created` with a `Location` header. `PUT` is a full replacement
  (omitted optional fields are cleared) and never creates a missing resource.
  `DELETE` returns `204`, and `404` on repeat.
- Text is trimmed; blank optional fields become `null`. Names, titles, and emails
  are not unique. Updates are last-write-wins (no ETags).

### Applications

| Method | Path | Success |
| --- | --- | --- |
| POST | `/api/v1/applications` | 201 + created resource |
| GET | `/api/v1/applications?limit=20&offset=0` | 200 + page |
| GET | `/api/v1/applications/{applicationId}` | 200 + resource |
| PUT | `/api/v1/applications/{applicationId}` | 200 + replaced resource |
| DELETE | `/api/v1/applications/{applicationId}` | 204 |

Request body (`title` required; all other fields optional):

```json
{
  "title": "Microsoft Garage",
  "version": "1.2.0",
  "company": "Microsoft",
  "website": "https://garage.example.com",
  "source": "https://github.com/example/garage",
  "license": "Apache-2.0",
  "description": "An internal application for managing Microsoft Garage locations and staff."
}
```

The response has the same fields plus `id`. `version` must follow SemVer 2.0.0;
`website` and `source` must be absolute HTTP(S) URLs without embedded credentials
(never fetched). Deleting an application removes only its maintainer links, never
the maintainers.

### Maintainers

| Method | Path | Success |
| --- | --- | --- |
| POST | `/api/v1/maintainers` | 201 + created resource |
| GET | `/api/v1/maintainers?limit=20&offset=0` | 200 + page |
| GET | `/api/v1/maintainers/{maintainerId}` | 200 + resource |
| PUT | `/api/v1/maintainers/{maintainerId}` | 200 + replaced resource |
| DELETE | `/api/v1/maintainers/{maintainerId}` | 204 |

```json
{ "name": "Garage Makers", "email": "garage.makers@example.com" }
```

Both fields are required. `name` is up to 200 characters of Unicode text; `email`
is up to 254 characters and validated for syntax only (`EmailAddressAttribute`, no
display-name forms, no DNS or deliverability checks). Maintainers are contact
metadata, not login identities. Deleting a maintainer removes only its links.

### Application–maintainer links

| Method | Path | Success |
| --- | --- | --- |
| PUT | `/api/v1/applications/{applicationId}/maintainers/{maintainerId}` | 204, link exists |
| DELETE | `/api/v1/applications/{applicationId}/maintainers/{maintainerId}` | 204, link absent |
| GET | `/api/v1/applications/{applicationId}/maintainers` | 200 + page of maintainers |
| GET | `/api/v1/maintainers/{maintainerId}/applications` | 200 + page of applications |

Link PUT and DELETE take no body and are idempotent: repeated or concurrent PUTs
produce one link, and DELETE of an absent link returns 204. Both resources must
exist (404 otherwise). Nested lists return 404 for a missing parent and an empty
page for a parent without links.

### Pagination

All list endpoints accept `limit` (default 20, 1–100) and `offset` (default 0,
0–10,000). Invalid values return 400; they are never clamped. Results are ordered
by ID ascending:

```json
{
  "items": [],
  "limit": 20,
  "offset": 0,
  "nextOffset": null
}
```

`nextOffset` is `offset + limit` only when more results exist (determined by
fetching `limit + 1` rows) and that offset is within range. Pages are not
snapshots; concurrent writes may shift results.

### Errors

All errors use `application/problem+json` and include a `traceId`. Validation
failures include a field-keyed `errors` object:

```json
{
  "type": "https://tools.ietf.org/html/rfc9110#section-15.5.1",
  "title": "One or more validation errors occurred.",
  "status": 400,
  "errors": { "email": ["Email must be a valid email address without a display name."] },
  "traceId": "00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01"
}
```

| Status | Meaning |
| --- | --- |
| 400 | Malformed JSON, invalid fields or query parameters, unknown properties, invalid/empty GUIDs |
| 401 | Missing or invalid API key (`WWW-Authenticate: Bearer`); checked before any resource lookup |
| 404 | Resource or link target does not exist; unknown route |
| 405 | Method not supported on the route (includes `Allow`) |
| 413 | Request body exceeds the configured limit |
| 415 | POST/PUT body is not JSON |
| 429 | Rate limit exceeded (`Retry-After` in seconds) |
| 500 | Unexpected failure; generic message, details only in server logs |
| 503 | Database lock wait exceeded (the write did not happen), or readiness check failed |

### Health

`GET /health/live` (process only) and `GET /health/ready` (opens and queries the
database) return `Healthy` or `503 Unhealthy`. Both are anonymous and exempt from
rate limiting.

## Production features

### ⭐ Observability (highlighted feature)

OpenTelemetry traces, metrics, and logs. This supports observability into our services to enable use to debug production failures, alert us of service unhealthiness, and create service dashboards in the future.

- **Traces:** one server span per request (health probes excluded). A tag
  allowlist keeps only method, status, route template, protocol, error type, and
  port, so raw URLs, IDs, query strings, headers, and bodies are never recorded.
  EF Core SQL spans are intentionally disabled to avoid capturing SQL and parameters.
- **Metrics:** built-in ASP.NET Core meters. `http.server.request.duration` gives
  request count and latency by `http.route` template, `http.server.active_requests`
  tracks concurrency, and `Microsoft.AspNetCore.RateLimiting` reports limiter
  rejections. Kestrel connection metrics are also collected.
- **Correlated logs:** JSON console logs include `TraceId`/`SpanId`, and each
  request logs one line with method, route template, and status. The `traceId` in
  every Problem Details response leads directly to the trace and logs.
- **Optional OTLP export:** set the standard `OTEL_EXPORTER_OTLP_ENDPOINT` (for
  example `http://localhost:4317`) to export all three signals. Without it, no
  exporter is registered and the API runs normally. Standard variables such as
  `OTEL_EXPORTER_OTLP_PROTOCOL` and `OTEL_METRIC_EXPORT_INTERVAL` apply.
- **Safeguards:** API keys, Authorization headers, request bodies, and maintainer
  emails are never collected. Export queues are bounded (512 items, 128 per
  batch) with one-second timeouts, and failed exports never block or fail
  requests (verified by a test that points at an unreachable endpoint).
- **Proven end to end:**
  [`ObservabilityE2ETests.Collector_ReceivesRequestTraceAndDurationMetric`](tests/MetadataApi.E2E.Tests/ObservabilityE2ETests.cs)
  starts the real API with `OTEL_EXPORTER_OTLP_ENDPOINT` pointed at a pinned
  `otel/opentelemetry-collector-contrib:0.147.0` collector in CI
  ([config](.github/otel-collector-config.yaml)). It sends
  `GET /api/v1/applications/{random-id}`, expects a 404, and reads the `traceId`
  from the Problem Details body. It then polls the collector's export file for up
  to 30 seconds until three things appear: that exact trace ID, the
  `http.server.request.duration` metric, and the route template
  `/api/v1/applications/{applicationId}`. Locally, the test is skipped unless
  `OTEL_SMOKE_EXPORT_FILE` (collector output file) and `OTEL_SMOKE_ENDPOINT` are
  set.

### Authentication

Authentication allows us gatekeep our API to a few select callers such as trusted users or services.

- A single shared API key, sent as `Authorization: Bearer <key>` and configured
  through `Authentication:ApiKey` (environment variable `Authentication__ApiKey`).
  Startup fails if the key is missing, blank, or shorter than 32 characters.
- Implemented as an ASP.NET Core authentication handler with a **fallback
  authorization policy**: every endpoint requires the key unless it explicitly
  opts out. Only the health probes and Development OpenAPI document opt out.
- Keys are compared in constant time over SHA-256 hashes. Every failure returns
  the same 401 before any resource lookup, so responses never reveal whether a
  resource exists. The key is never logged.
- All callers share administrative access (no per-user permissions). Store the
  key in a secret store, and require HTTPS for any public deployment.

### Reliability

For reliability, we implemented configurable rate limiting, body request size limits, health probes, input validation, etc. to automatically resolve or prevent issues. Overall these make our service more robust to failures.

| Setting | Environment variable | Default |
| --- | --- | --- |
| `RateLimiting:PermitLimit` | `RateLimiting__PermitLimit` | 1000 requests |
| `RateLimiting:WindowSeconds` | `RateLimiting__WindowSeconds` | 60 seconds |
| `Limits:MaxRequestBodyBytes` | `Limits__MaxRequestBodyBytes` | 65536 bytes (64 KiB) |

All settings are validated at startup.

- **Rate limiting:** a built-in global fixed-window limiter with no queue returns
  429 with `Retry-After`. It runs **before authentication**, so unauthenticated
  floods are throttled too. The limit is per process, not per client.
- **Request body limit (OOM prevention):** enforced before JSON deserialization,
  for both `Content-Length` and chunked bodies; oversized bodies return 413. List
  responses are always bounded by pagination, and telemetry queues are bounded.
- **Bounded database contention:** a five-second SQLite lock wait, then 503.
  There are no blanket retries, because replaying a POST could create duplicates.
- **Safe failures:** centralized exception handling returns a generic 500 with a
  `traceId`; stack traces and database details stay in server logs.
- **Health probes:** separate liveness and readiness endpoints for orchestrators.
- **Input validation:** strict JSON, field rules, and GUID/pagination validation
  at the API boundary. All field errors are reported together.

## Storage

For the purposes of this project, we went with an embedded SQLite database, interfaced via EF core. Using an embedded database over an external one was primarily due to time constraints and limited scope of this project. EF core is .NET's official ORM, so naturally we'd use that the C# interface with our database. We configured our DB to prioritize correctness over speed since our application isn't particularly time-sensitive.

- **Consistency:** Strong consistency. With a single database, every read gets
  the latest committed write. Foreign keys and a composite primary key keep links
  valid and unique.
- **Durability:** `synchronous=FULL` flushes each write to disk before the API
  responds. This trades some write speed for not losing acknowledged data.
- **Scaling:** reads scale well, because WAL mode lets them run alongside a write,
  but SQLite allows only one writer at a time and only one server instance.

**Trade-off:** SQLite gives up multi-instance and high-write scale in exchange
for strong guarantees with no database server to setup.

Configuration and operations:

- The database file is `Database:Path` (`Database__Path`), defaulting to
  `metadata.db` in `src/MetadataApi`. Use the same value for migrations and for
  the server.
- Schema changes go through explicit EF Core migrations
  (`dotnet ef database update --project src\MetadataApi`). Startup refuses to run
  with pending migrations rather than changing the schema automatically.
- **Backup:** stop the API and copy the database together with any WAL file, or
  use SQLite's online backup API / `.backup` command. Never copy only the main
  file while the API is running.

## Testing and CI

| Suite | Project | What it covers |
| --- | --- | --- |
| Unit | `tests/MetadataApi.Tests` | Validation and normalization (length boundaries, SemVer, URLs, control characters), pagination, authentication and options validation |
| SQLite integration | `tests/MetadataApi.Tests` | Migrations, pragmas/WAL, foreign keys, duplicate links, cascades, rollback, bounded lock contention, persistence after reopen |
| In-process API | `tests/MetadataApi.Tests` | Full HTTP contracts through `WebApplicationFactory` against real SQLite: CRUD, links, pagination, errors, 401/413/429/500/503, telemetry privacy and correlation |
| Real HTTP E2E | `tests/MetadataApi.E2E.Tests` | Starts the built API as a separate Kestrel process (Production mode, temporary database, random API key) and tests over loopback HTTP |

The E2E suite covers CRUD for both resources, link lifecycle, deletion integrity,
pagination, validation, authentication, persistence across a server restart, 413
and 429 (in an isolated low-limit fixture), and the collector smoke test. Tests
wait for readiness rather than sleeping and use bounded timeouts. They clean up
processes and temporary databases afterward. Server logs are kept under
`artifacts/e2e/<run>/`. Each fixture's `http-trace.log` records every real request
(including unauthenticated clients, readiness checks, restarts, and low-limit tests):
method, path + query, response status, and elapsed milliseconds to response headers.
Transport failures are marked `ERROR`. Traces never inspect headers or bodies, so
Authorization/API keys and maintainer email bodies are excluded. These traces are
per fixture rather than per test because clients are shared across tests.

```powershell
dotnet tool restore
dotnet build MetadataApi.slnx --configuration Release

# Same TRX loggers and output locations as CI (start with an empty artifacts directory).
dotnet test tests\MetadataApi.Tests\MetadataApi.Tests.csproj --configuration Release --no-build --collect:"XPlat Code Coverage" --logger "trx;LogFileName=unit-integration.trx" --results-directory artifacts\unit-integration
dotnet test tests\MetadataApi.E2E.Tests\MetadataApi.E2E.Tests.csproj --configuration Release --no-build --logger "trx;LogFileName=e2e.trx" --results-directory artifacts\e2e-results

# Python 3, standard library only; writes artifacts\test-summary.md locally.
python .github\scripts\test_summary.py
```

On Windows, a running Debug API locks its binaries; use `--configuration Release`
to test alongside it.

The [GitHub Actions workflow](.github/workflows/ci.yml) runs on every push and pull
request:

1. Restore and build (SDK pinned by `global.json`).
2. Run unit and integration tests with Coverlet coverage.
3. Generate a masked, one-time API key and start the OpenTelemetry collector.
4. Run the real HTTP E2E suite against the API process.
5. Publish test evidence and upload artifacts, **even on failure**. E2E still runs
   after a unit-test failure if the Release build succeeded.

The job summary contains:

- **Unit & SQLite integration** and **Real HTTP E2E (Kestrel process)**: every
  discovered test case grouped by class, including theory cases, with pass/fail/skip
  and duration. xUnit configuration turns method underscores into spaces without
  mass-renaming tests. The collector smoke test runs in CI; locally it is explicitly
  skipped unless `OTEL_SMOKE_EXPORT_FILE` and `OTEL_SMOKE_ENDPOINT` point to a running
  collector configured like [the CI collector](.github/otel-collector-config.yaml).
- **E2E HTTP coverage (observed real requests)**: endpoint templates (GUIDs →
  `{id}`), observed status counts (including 401/404/413/415/429), and total requests.
  Query variants are grouped by path. This is real traffic evidence, not a claim of
  exhaustive endpoint/error coverage; the per-test assertions establish behavior.
- **Unit and integration coverage**: total line/branch percentages plus a
  per-assembly/per-class breakdown from Coverlet's Cobertura report.

Failures also appear as **Actions error annotations**, with the suite, class,
test name, and failure details. Missing report files are reported explicitly.
The **test-results** artifact retains TRX, raw Cobertura, the rendered
`test-summary.md`, per-fixture HTTP/migration/server logs, and collector telemetry
and logs for seven days. Raw HTTP traces retain query strings; do not add secrets
to request URLs.

Reporting uses the built-in VSTest TRX logger and a small Python standard-library
renderer, rather than a check-writing action or third-party logger. This needs no
extra NuGet packages, external service, secrets, or `checks: write` permission, and
works unchanged on push and fork pull requests with `contents: read`. No external
coverage badge is used. The workflow does not grant elevated PR permissions.

Runs: [Actions → CI](https://github.com/adrielmsft/aks-hyperscale-metadata-api/actions/workflows/ci.yml)

## Key design decisions

- **Separate data model for the Maintainer–Application relationship.** A
  dedicated `ApplicationMaintainers` table (composite key
  `(ApplicationId, MaintainerId)`) links the two resources instead of embedding
  maintainers in each application. This supports many-to-many relationships and keeps the
  relationship separate from each resource's properties.
- **GUID IDs, never names in paths.** Titles and names can change or
  repeat. Having a generated GUID guarantees uniqueness.
- **Correctness over write throughput.** SQLite runs with `synchronous=FULL`,
  foreign keys, a bounded lock wait, and no blind retries. This assumes the nature of this application isn't time-sensitive and correctness is essential.
- **Last-write-wins updates.** Simple last-write-wins over optimistic concurrency due to time constraints and assumption of low risk of conflict for this type of data.
- **Offset pagination vs. Cursor pagination** Went with offset due to simplicity and assuming modest scale requirements.
- **Shared API key** Assumes API is only called by a few trusted services. Quick to implement given time constraints, but next step would be to support per-client keys or use an identity provider if the API needs to be used at a large scale.

## Cost
~9 hours of active work throughout the day
