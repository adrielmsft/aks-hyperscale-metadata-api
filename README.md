# Metadata API

A RESTful ASP.NET Core (.NET 10) API for managing **Application** and **Maintainer**
metadata and the many-to-many links between them. Data is stored in file-backed
SQLite through EF Core. See the [design document](aks-metadata-api-design.md) for
the full requirements, validation scheme, and rationale.

[![CI](https://github.com/adrielmsft/aks-hyperscale-metadata-api/actions/workflows/ci.yml/badge.svg)](https://github.com/adrielmsft/aks-hyperscale-metadata-api/actions/workflows/ci.yml)

## Contents

- [Quick start](#quick-start)
- [API reference](#api-reference)
- [Production features](#production-features)
- [Storage](#storage)
- [Testing and CI](#testing-and-ci)
- [Key design decisions](#key-design-decisions)

## Quick start

Prerequisites: [.NET SDK 10.0.402](https://dotnet.microsoft.com/download/dotnet/10.0)
(or a newer 10.0.4xx patch; pinned in `global.json`) and Git. No database service,
cloud account, or committed secrets are needed.

From the repository root in PowerShell:

```powershell
# 1. Generate a local API key (required; at least 32 characters)
$env:Authentication__ApiKey = [Convert]::ToHexString([System.Security.Cryptography.RandomNumberGenerator]::GetBytes(32))

# 2. Restore, build, create the database, and run
dotnet tool restore
dotnet build MetadataApi.slnx
dotnet ef database update --project src\MetadataApi
dotnet run --project src\MetadataApi --launch-profile http
```

The API listens at `http://localhost:5108`. In another terminal, set
`$env:Authentication__ApiKey` to the same key, then run:

```powershell
$headers = @{ Authorization = "Bearer $env:Authentication__ApiKey" }
$app = Invoke-RestMethod -Method Post -Uri http://localhost:5108/api/v1/applications -Headers $headers -ContentType application/json -Body '{"title":"Microsoft Garage","version":"1.2.0"}'
Invoke-RestMethod -Uri "http://localhost:5108/api/v1/applications/$($app.id)" -Headers $headers
curl.exe --fail http://localhost:5108/health/ready
```

Run all tests (unit, SQLite integration, and real-HTTP E2E):

```powershell
dotnet tool restore
dotnet test MetadataApi.slnx --configuration Release
```

[MetadataApi.http](src/MetadataApi/MetadataApi.http) contains ready-made successful
and failing requests; it reads the key from the `Authentication__ApiKey` environment
variable. On Linux/macOS, use `/` path separators and `curl` instead of `curl.exe`.

## API reference

In Development, the OpenAPI document is served at `GET /openapi/v1.json`.

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

OpenTelemetry traces, metrics, and logs, designed to be safe by default and
optional to export.

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
- **Proven end to end:** CI runs a pinned `otel/opentelemetry-collector-contrib:0.147.0`
  ([config](.github/otel-collector-config.yaml)). An E2E test sends a real request,
  takes the `traceId` from the response, and waits until that trace and the
  request-duration metric appear in the collector's output. Locally, the test is
  skipped unless `OTEL_SMOKE_EXPORT_FILE` (collector output file) and
  `OTEL_SMOKE_ENDPOINT` are set.

### Authentication

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

Middleware order: observability → exception handling → rate limiting and body
limits → authentication → authorization → endpoints.

## Storage

**File-backed SQLite via EF Core**, sized for a single API instance on one machine.

- **Consistency:** foreign keys are enforced on every connection, a composite
  primary key `(ApplicationId, MaintainerId)` prevents duplicate links, and
  deletions cascade to link rows only. Each write runs in a single transaction;
  link changes check both parents and write inside one short transaction.
  Duplicate-link PUTs use `INSERT ... ON CONFLICT DO NOTHING`.
- **Durability:** WAL journal mode with `synchronous=FULL`, so a committed write
  is synced to disk before the response returns, provided the storage honors
  sync requests. Data survives restarts (verified by E2E tests).
- **Concurrency:** WAL lets readers proceed alongside one writer. Writers wait up
  to five seconds for the lock before returning 503.
- **Scaling:** suited to one instance. Reads are indexed and paginated in the
  database. SQLite allows one writer at a time and must not be shared over network
  storage. Multiple replicas or heavy write load would require moving to
  PostgreSQL or SQL Server, which EF Core supports with provider-specific
  migrations.

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
`artifacts/e2e`.

```powershell
# Real HTTP E2E only
dotnet test tests\MetadataApi.E2E.Tests\MetadataApi.E2E.Tests.csproj --configuration Release

# Unit/integration coverage (Cobertura XML under artifacts\TestResults)
dotnet test tests\MetadataApi.Tests\MetadataApi.Tests.csproj --collect:"XPlat Code Coverage" --results-directory artifacts\TestResults
```

On Windows, a running Debug API locks its binaries; use `--configuration Release`
to test alongside it.

The [GitHub Actions workflow](.github/workflows/ci.yml) runs on every push and pull
request:

1. Restore and build (SDK pinned by `global.json`).
2. Run unit and integration tests with Coverlet coverage.
3. Generate a masked, one-time API key and start the OpenTelemetry collector.
4. Run the real HTTP E2E suite against the API process.
5. Publish line and branch coverage to the job summary. Upload test results,
   coverage, server logs, and collector output, including on failure.

Runs: [Actions → CI](https://github.com/adrielmsft/aks-hyperscale-metadata-api/actions/workflows/ci.yml)

## Key design decisions

- **Many-to-many links managed as a sub-resource.** Applications and maintainers
  have independent lifecycles; idempotent `PUT`/`DELETE` on the link path avoids
  a separate association ID.
- **GUID IDs, never names in paths.** Titles, names, and emails can change or
  repeat.
- **Strict input over lenient input.** Unknown properties and out-of-range
  pagination values are rejected, not ignored or clamped, so client bugs show up
  immediately.
- **Correctness over write throughput.** SQLite runs with `synchronous=FULL`,
  foreign keys, a bounded lock wait, and no blind retries.
- **Last-write-wins updates.** Optimistic concurrency (ETags) is a documented
  non-goal for this scope; transactions protect integrity but not against lost
  updates.
- **Offset pagination capped at 10,000.** Simple and predictable for this size; a
  larger catalog would use cursor pagination.
- **Shared API key instead of an identity provider.** Secure and standard for a
  single-tenant service API. Per-client keys or Entra ID/OAuth would be the next
  step.
- **Observability that is off by default to export but always safe.**
  Instrumentation is always on; export is opt-in through standard OTel settings.
  Privacy is enforced by allowlists rather than denylists.
- **Out of scope:** PATCH, search, bulk operations, idempotency keys, request
  timeouts (would need tests of what happens when a write is cancelled mid-commit),
  distributed rate limiting, dashboards and alerting.
