# Metadata API

An ASP.NET Core API for application and maintainer metadata. See the
[design document](aks-metadata-api-design.md) for the planned behavior and implementation milestones.

## Current status

This first increment scaffolds the API and xUnit test project. It includes a
liveness endpoint, development-only OpenAPI generation, and Problem Details for
HTTP errors. CRUD, SQLite persistence, authentication, telemetry, and GitHub
Actions are not implemented yet.

## Prerequisites

- [.NET SDK 10.0.402](https://dotnet.microsoft.com/download/dotnet/10.0), or a newer
  patch in the 10.0.4xx feature band. SDK selection is pinned in `global.json`;
  prerelease SDKs and other feature bands are not selected automatically.
- Git. No database service, cloud account, or secrets are needed for this increment.

Run the following commands from the repository root.

## Build and run

```powershell
dotnet build MetadataApi.slnx
dotnet run --project src\MetadataApi --launch-profile http
```

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

## Test

```powershell
dotnet test MetadataApi.slnx
```

The xUnit project currently contains in-process API smoke tests using
`WebApplicationFactory`: liveness, OpenAPI availability in Development and its
absence in Production, and Problem Details for missing routes. These are
integration checks of the scaffold, not the planned separate-process E2E suite.
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