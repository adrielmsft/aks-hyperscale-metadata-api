# Metadata API

An ASP.NET Core API for application and maintainer metadata. See the
[design document](aks-metadata-api-design.md) for the planned behavior and implementation milestones.

## Current status

The API manages application and maintainer metadata using SQLite and EF Core.
See the API reference below for currently implemented operations. Liveness,
OpenAPI, input validation, Problem Details, and real HTTP CI tests are available.
Authentication and telemetry are not implemented yet.

## Prerequisites

- [.NET SDK 10.0.402](https://dotnet.microsoft.com/download/dotnet/10.0), or a newer
  patch in the 10.0.4xx feature band. SDK selection is pinned in `global.json`;
  prerelease SDKs and other feature bands are not selected automatically.
- Git. No database service, cloud account, or secrets are needed for this increment.

Run the following commands from the repository root.

## Build and run

```powershell
dotnet tool restore
dotnet build MetadataApi.slnx
dotnet ef database update --project src\MetadataApi
dotnet run --project src\MetadataApi --launch-profile http
```

The migration command creates or updates the database without deleting existing
records. Run it again after pulling changes that add migrations. Startup rejects
pending migrations instead of automatically modifying the schema.

The API listens at `http://localhost:5108` in Development mode:

- `GET /health/live` returns `200 OK` with the plain-text body `Healthy`.
  This checks process liveness only, not database readiness.
- `GET /openapi/v1.json` returns the generated OpenAPI document describing
  Application operations; the liveness endpoint is not included.
- Unknown routes return `404` Problem Details.

Use [MetadataApi.http](src/MetadataApi/MetadataApi.http) with an HTTP request client,
or verify liveness from another terminal:

```powershell
curl.exe --fail http://localhost:5108/health/live
```

Stop the server with Ctrl+C. Authentication is not implemented yet; keep the
server bound to localhost and do not expose it publicly.

## Application API

| Method | Path | Success |
| --- | --- | --- |
| POST | `/api/v1/applications` | 201 with the created resource and a Location header |
| GET | `/api/v1/applications?limit=20&offset=0` | 200 with a bounded page of resources |
| GET | `/api/v1/applications/{applicationId}` | 200 with the stored resource |
| PUT | `/api/v1/applications/{applicationId}` | 200 with fully replaced metadata |
| DELETE | `/api/v1/applications/{applicationId}` | 204 with no body |

Create and fetch an application from PowerShell:

```powershell
$application = Invoke-RestMethod -Method Post -Uri http://localhost:5108/api/v1/applications -ContentType application/json -Body '{"title":"Inventory Portal","version":"1.2.0"}'
Invoke-RestMethod -Uri "http://localhost:5108/api/v1/applications/$($application.id)"
Invoke-RestMethod -Uri "http://localhost:5108/api/v1/applications?limit=20&offset=0"
Invoke-RestMethod -Method Put -Uri "http://localhost:5108/api/v1/applications/$($application.id)" -ContentType application/json -Body '{"title":"Microsoft Garage","version":"2.0.0"}'
Invoke-RestMethod -Method Delete -Uri "http://localhost:5108/api/v1/applications/$($application.id)"
```

Example create response (the server generates the ID):

```json
{
  "id": "d29fa39b-1bcb-4cf4-a841-a1eae83e0486",
  "title": "Inventory Portal",
  "version": "1.2.0",
  "company": null,
  "website": null,
  "source": null,
  "license": null,
  "description": null
}
```

Title is required; all other metadata is optional. Text is trimmed and blank
optional fields become null. Versions, when supplied, must follow SemVer 2.0.0.
Website and Source must be absolute HTTP(S) URLs without embedded credentials;
the API never fetches them. Names/titles are not unique: repeated POST requests
create distinct resources. Creation does not add maintainer associations.

PUT is a full replacement, not a partial update: Title is required and omitted
optional fields become null. The ID and existing maintainer links do not change.
A missing application is not created. Updates use last-write-wins, with no ETag
or optimistic concurrency check. DELETE removes the application and its links
only, never its maintainers or other applications. Repeat deletion returns 404.

Collection GET defaults to `limit=20` and `offset=0`. Limit must be an integer
from 1 through 100 and offset an integer from 0 through 10,000; invalid values
return 400, not clamped results. Results are ordered by ID ascending in SQLite.
The API fetches at most `limit + 1` rows and returns an envelope:

```json
{
  "items": [],
  "limit": 20,
  "offset": 0,
  "nextOffset": null
}
```

`items` contains Application representations (including explicit null optional
fields). `nextOffset` is `offset + limit` only if more rows exist and that offset
is at most 10,000; otherwise it is null. Empty and out-of-data pages have
`items: []`. Pagination is not a snapshot: concurrent writes may shift results.

JSON uses exact camelCase property names. Unknown properties (including a supplied
`id`), duplicate properties, and incorrect value types are rejected. See the
[validation scheme](aks-metadata-api-design.md#data-model-input-validation)
for field limits.

Errors use `application/problem+json`:

- **400:** malformed JSON, invalid fields or pagination, or invalid/empty GUIDs. Metadata and
  ID validation errors include a field-keyed `errors` object.
- **404:** a valid application ID does not exist.
- **415:** the POST or PUT body does not use a supported JSON content type.
- **503:** database lock contention exhausted the bounded wait; the write did not succeed.
- **500:** an unexpected failure, without internal exception details.

Use the Development OpenAPI document for operation schemas and
[MetadataApi.http](src/MetadataApi/MetadataApi.http) for successful and failing requests.

### Associations

Manage links separately from resource metadata; applications and maintainers may
have zero or more links. There is no association ID or request body.

| Method | Path | Success |
| --- | --- | --- |
| PUT | `/api/v1/applications/{applicationId}/maintainers/{maintainerId}` | 204, ensure the link exists |
| DELETE | `/api/v1/applications/{applicationId}/maintainers/{maintainerId}` | 204, ensure the link is absent |
| GET | `/api/v1/applications/{applicationId}/maintainers?limit=20&offset=0` | 200, page of Maintainer resources |
| GET | `/api/v1/maintainers/{maintainerId}/applications?limit=20&offset=0` | 200, page of Application resources |

PUT and DELETE require both resources to exist (404 otherwise). Repeated PUTs,
including concurrent duplicates, create just one link; repeated DELETEs return
204 even if the link is already absent. Both mutations return no content and
never modify resource metadata. Invalid or empty GUIDs return 400 validation
Problem Details with field-keyed errors.

Nested lists use the same `items`, `limit`, `offset`, and `nextOffset` envelope
and pagination rules described above: limit 1–100 (default 20), offset 0–10,000
(default 0), returned-resource ID ascending, and a `limit + 1` database fetch.
A missing parent returns 404; an existing parent without links returns an empty
page. Concurrent writes may shift pages.

The initial migration already supplies the composite primary key, foreign keys,
cascading link deletion, and reverse-lookup index; no additional migration is
needed. Short SQLite write transactions protect parent checks and link changes;
a parameterized `ON CONFLICT (ApplicationId, MaintainerId) DO NOTHING` insert
makes duplicate PUTs successful without suppressing other constraint failures.
Resource updates preserve links; deleting either resource removes only its own
links, never the resources on the other side. Links persist across restarts of
the API using the same database file. SQLite/service tests, API tests, and
real-Kestrel E2E tests cover these guarantees; the E2E suite also verifies link
removal and both resource-deletion directions across restart.

## Maintainer API

| Method | Path | Success |
| --- | --- | --- |
| POST | `/api/v1/maintainers` | 201 with the created resource and a Location header |
| GET | `/api/v1/maintainers/{maintainerId}` | 200 with the stored resource |
| PUT | `/api/v1/maintainers/{maintainerId}` | 200 with the fully replaced resource |
| DELETE | `/api/v1/maintainers/{maintainerId}` | 204 with no body |
| GET | `/api/v1/maintainers?limit=20&offset=0` | 200 with a bounded page |

Create or fully replace contact metadata with a JSON body:

```json
{
  "name": "Garage Makers",
  "email": "Garage.Makers@example.com"
}
```

Example resource response (POST generates the ID; PUT keeps it unchanged):

```json
{
  "id": "54b28c61-8ec5-42c1-966b-3564c59bf965",
  "name": "Garage Makers",
  "email": "Garage.Makers@example.com"
}
```

Both `name` and `email` are required on POST and PUT; omitted, null, or blank
values return 400. Text is trimmed before validation and storage, preserving
internal whitespace and casing. Names allow Unicode and are limited to 200
UTF-16 code units; email is limited to 254. Single-line fields reject control
characters. Email uses .NET `EmailAddressAttribute` basic syntax validation and
rejects display-name forms such as `Garage <garage@example.com>`. No DNS,
ownership, or deliverability checks are performed. Duplicate names and emails
are allowed: maintainers are contact metadata, not authenticated identities.

JSON uses exact camelCase property names. Unknown properties (including `id`),
duplicate properties, malformed JSON, and wrong value types return 400.
IDs are server-generated, immutable, non-empty GUIDs. Invalid/empty route GUIDs
return 400; syntactically valid missing IDs return 404, including on PUT and
DELETE. PUT is a full replacement, not an upsert; last write wins without
optimistic concurrency. Updates preserve application links. Deletion removes
only the maintainer and its links, never the associated applications.

Collection responses use this envelope:

```json
{
  "items": [
    {
      "id": "54b28c61-8ec5-42c1-966b-3564c59bf965",
      "name": "Garage Makers",
      "email": "Garage.Makers@example.com"
    }
  ],
  "limit": 20,
  "offset": 0,
  "nextOffset": null
}
```

`limit` defaults to 20 and must be an integer from 1 through 100. `offset`
defaults to 0 and must be an integer from 0 through 10000. Invalid values
return 400, never clamping. Results are ordered in SQLite by ID ascending and
use bounded offset pagination with one-row lookahead. `nextOffset` is present
as a number only when more records exist and the next offset is supported;
otherwise it is null. Empty or beyond-end pages return 200 with empty `items`.
Pages are not snapshots: concurrent writes can change subsequent pages.

Errors use `application/problem+json`, with field-keyed `errors` for metadata,
route-ID, and pagination validation. POST and PUT require a supported JSON
content type (otherwise 415). Database contention returns 503 after the existing
five-second bounded lock wait, without changing the requested resource.
Unexpected failures return 500 without exposing exception details.

Maintainer validation and service tests, real SQLite-backed API tests, and
separate-process `MaintainerE2ETests` cover CRUD, replacement, duplicates,
Unicode and length boundaries, rejected input, pagination, persistence after
restart, cascade safety, cancellation, and bounded contention failures.

## Database configuration

- The default database is `metadata.db` under the API project's content root
  (`src/MetadataApi` for the commands above). Data survives process restarts.
- Set `Database__Path` to an absolute path to use another location. Use the same
  value for migration commands and the running server:

  ```powershell
  $env:Database__Path = Join-Path $PWD "artifacts\local\metadata.db"
  dotnet ef database update --project src\MetadataApi
  dotnet run --project src\MetadataApi --launch-profile http
  ```

- Connections enforce foreign keys and `synchronous=FULL`. Startup establishes
  and verifies WAL mode. A five-second command/lock timeout bounds database
  contention; no additional application retry loop is installed.
- GUID primary keys identify resources. A composite association key prevents
  duplicate links, and foreign keys cascade parent deletion to links only.
  Application create requests are validated before persistence.
- Connection pooling is disabled to keep connection configuration and file
  cleanup predictable for this single-instance implementation.
- Keep the database on persistent local storage, not an ephemeral container
  filesystem or shared network drive. Durability depends on storage honoring
  synchronization requests. SQLite allows only one writer at a time.
- Database and journal files are excluded from Git. Migrations and the pinned
  `dotnet-ef` tool manifest are committed as source configuration.

For a manual backup, stop the API and any other database connections before
copying the database; retain any remaining WAL file with it. For an online backup,
use SQLite's backup API or `.backup` command rather than copying a live main file
alone. Automated backup infrastructure is outside the current scope.

To add a future schema migration after modifying the EF model:

```powershell
dotnet ef migrations add DescribeSchemaChange --project src\MetadataApi --output-dir Persistence\Migrations
```

Review generated migrations for destructive changes before applying them.

## Test

```powershell
dotnet tool restore
dotnet test MetadataApi.slnx
```

The unit/integration xUnit project contains:

- In-process API smoke tests using `WebApplicationFactory`: liveness, OpenAPI
  availability in Development and its absence in Production, and Problem Details
  for missing routes.
- Application HTTP tests against real SQLite: complete CRUD, Location headers,
  replacement/null clearing, unchanged identity and associations, link-only
  cascade deletion, pagination ordering/defaults/bounds/traversal cap, invalid
  input, missing resources, OpenAPI metadata, generic 500 errors, and unchanged
  storage after 503 responses during database contention.
- Unit tests for Application normalization and validation, including exact length
  boundaries, SemVer syntax, URL rules, and control characters for create and
  replacement; pagination boundaries/next offsets and service cancellation.
- Real SQLite integration tests: migrations, connection settings, persistence
  after reopening, foreign keys, duplicate links, cascading deletion, rollback,
  bounded lock contention, and startup rejection of pending migrations.

Tests create isolated temporary databases, apply real migrations, and remove
their files during cleanup; no manual database setup is required for tests.

The separate [E2E project](tests/MetadataApi.E2E.Tests) starts the built API as a
real child process running Kestrel in Production mode, applies migrations to its
own temporary SQLite database, and sends requests over a real loopback TCP port.
It does not use `WebApplicationFactory`, mock storage, or shared API DTOs.

E2E checks cover complete Application CRUD, Location headers, normalized metadata,
full replacement and explicit null clearing, duplicate titles, paginated listing,
validation and JSON errors, 404/415 responses, and persistence of creation,
replacement, and deletion across API process restarts. Kestrel chooses a free port;
startup waits for its listening signal and verifies liveness rather than sleeping
for a fixed duration. Startup is bounded, test HTTP calls have timeouts, and the
fixture stops its own processes and deletes its temporary database on completion.
Migration and server logs remain under `artifacts/e2e` for diagnosis.

Run just the real HTTP suite:

```powershell
dotnet tool restore
dotnet test tests\MetadataApi.E2E.Tests\MetadataApi.E2E.Tests.csproj --configuration Release
```

No manually running server is needed. This command builds the API through a
build-only project reference; the tests communicate with it exclusively over HTTP.

To collect unit/integration coverage using the included Coverlet collector:

```powershell
dotnet test tests\MetadataApi.Tests\MetadataApi.Tests.csproj --collect:"XPlat Code Coverage" --results-directory artifacts\TestResults
```

Coverage reports are written as `coverage.cobertura.xml` under the results
directory. Generated build and test outputs are excluded from Git.

On Windows, stop a running Debug API before rebuilding its binaries. To test
without interrupting that server, use separate Release outputs:

```powershell
dotnet test MetadataApi.slnx --configuration Release
```

## GitHub Actions

The [CI workflow](.github/workflows/ci.yml) runs on pushes, pull requests, and
manual dispatches:

1. Install the SDK selected by `global.json`, restore local tools and packages,
   and build Release.
2. Run unit/SQLite integration tests and collect Coverlet coverage.
3. Run the real HTTP E2E suite, which migrates, starts, exercises, restarts, and
   stops its own API process.
4. Write line/branch coverage to the Actions job summary and upload TRX results,
   Cobertura coverage, and test-only migration/server logs, including on failure.

No cloud deployment, database service, or production credentials are required.
The API currently has no authentication; tests use only synthetic metadata.
Logs do not include request bodies or credentials.

See the [Actions workflow page](https://github.com/adrielmsft/aks-hyperscale-metadata-api/actions/workflows/ci.yml).
A direct link to a successful run must be added here after this workflow is
pushed and passes on GitHub; local test results alone do not satisfy that
submission requirement. Extend E2E coverage as the remaining operations and
production features are implemented.

Commands above use Windows PowerShell paths. On Linux or macOS, use
`src/MetadataApi` and `artifacts/TestResults` for the corresponding paths, and
`curl` instead of `curl.exe`.