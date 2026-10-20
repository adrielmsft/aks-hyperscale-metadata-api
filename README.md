# Metadata API

An ASP.NET Core API for application and maintainer metadata. See the
[design document](aks-metadata-api-design.md) for the planned behavior and implementation milestones.

## Current status

The API scaffold includes file-backed SQLite persistence with EF Core, an initial
migration for Applications, Maintainers, and their associations, a liveness
endpoint, development-only OpenAPI generation, and Problem Details for HTTP
errors. CRUD endpoints, authentication, telemetry, and GitHub Actions are not
implemented yet.

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
- `GET /openapi/v1.json` returns the generated OpenAPI document. There are no
  resource operations yet; the liveness endpoint is not included in the document.
- Unknown routes return `404` Problem Details.

Use [MetadataApi.http](src/MetadataApi/MetadataApi.http) with an HTTP request client,
or verify liveness from another terminal:

```powershell
curl.exe --fail http://localhost:5108/health/live
```

Stop the server with Ctrl+C. Authentication is not implemented yet; keep the
server bound to localhost and do not expose it publicly.

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
  Request-level field validation will be implemented with CRUD.
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
- Real SQLite integration tests: migrations, connection settings, persistence
  after reopening, foreign keys, duplicate links, cascading deletion, rollback,
  bounded lock contention, and startup rejection of pending migrations.

Tests create isolated temporary databases, apply real migrations, and remove
their files during cleanup; no manual database setup is required for tests.
These are integration checks, not the planned separate-process E2E suite.
Unit tests will be added alongside application logic.

To collect coverage using the included Coverlet collector:

```powershell
dotnet test MetadataApi.slnx --collect:"XPlat Code Coverage" --results-directory artifacts\TestResults
```

Coverage reports are written as `coverage.cobertura.xml` under the results
directory. Generated build and test outputs are excluded from Git.

Commands above use Windows PowerShell paths. On Linux or macOS, use
`src/MetadataApi` and `artifacts/TestResults` for the corresponding paths, and
`curl` instead of `curl.exe`.