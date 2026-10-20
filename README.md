# Metadata API

An ASP.NET Core API for application and maintainer metadata. See the
[design document](aks-metadata-api-design.md) for the planned behavior and implementation milestones.

## Current status

The first Application slice supports creating and retrieving metadata backed by
SQLite and EF Core. The schema also includes Maintainers and their associations.
Liveness, development OpenAPI, input validation, and Problem Details are available.
Updates, deletion, listing, Maintainer/association endpoints, authentication,
telemetry, and GitHub Actions are not implemented yet.

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
| GET | `/api/v1/applications/{applicationId}` | 200 with the stored resource |

Create and fetch an application from PowerShell:

```powershell
$application = Invoke-RestMethod -Method Post -Uri http://localhost:5108/api/v1/applications -ContentType application/json -Body '{"title":"Inventory Portal","version":"1.2.0"}'
Invoke-RestMethod -Uri "http://localhost:5108/api/v1/applications/$($application.id)"
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

JSON uses exact camelCase property names. Unknown properties (including a supplied
`id`), duplicate properties, and incorrect value types are rejected. See the
[validation scheme](aks-metadata-api-design.md#data-model-input-validation)
for field limits.

Errors use `application/problem+json`:

- **400:** malformed JSON, invalid fields, or invalid/empty GUIDs. Metadata and
  ID validation errors include a field-keyed `errors` object.
- **404:** a valid application ID does not exist.
- **415:** the POST body does not use a supported JSON content type.
- **503:** database lock contention exhausted the bounded wait; no resource was created.
- **500:** an unexpected failure, without internal exception details.

Use the Development OpenAPI document for operation schemas and
[MetadataApi.http](src/MetadataApi/MetadataApi.http) for successful and failing requests.

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
dotnet test MetadataApi.slnx
```

The xUnit project contains:

- In-process API smoke tests using `WebApplicationFactory`: liveness, OpenAPI
  availability in Development and its absence in Production, and Problem Details
  for missing routes.
- Application HTTP tests against real SQLite: create/read behavior, Location
  headers, null fields, invalid input, missing resources, OpenAPI metadata, and
  503 responses during database contention.
- Unit tests for Application normalization and validation, including exact length
  boundaries, SemVer syntax, URL rules, and control characters.
- Real SQLite integration tests: migrations, connection settings, persistence
  after reopening, foreign keys, duplicate links, cascading deletion, rollback,
  bounded lock contention, and startup rejection of pending migrations.

Tests create isolated temporary databases, apply real migrations, and remove
their files during cleanup; no manual database setup is required for tests.
These are integration checks, not the planned separate-process E2E suite.

To collect coverage using the included Coverlet collector:

```powershell
dotnet test MetadataApi.slnx --collect:"XPlat Code Coverage" --results-directory artifacts\TestResults
```

Coverage reports are written as `coverage.cobertura.xml` under the results
directory. Generated build and test outputs are excluded from Git.

On Windows, stop a running Debug API before rebuilding its binaries. To test
without interrupting that server, use separate Release outputs:

```powershell
dotnet test MetadataApi.slnx --configuration Release
```

Commands above use Windows PowerShell paths. On Linux or macOS, use
`src/MetadataApi` and `artifacts/TestResults` for the corresponding paths, and
`curl` instead of `curl.exe`.