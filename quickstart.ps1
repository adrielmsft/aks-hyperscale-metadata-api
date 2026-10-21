<#
.SYNOPSIS
    One-command setup and run for the Metadata API.

.DESCRIPTION
    Ensures the .NET SDK pinned in global.json is available (installing it into
    ./.dotnet if needed), restores tools and packages, builds, applies database
    migrations, generates a local API key, and starts the API.
    Works with Windows PowerShell 5.1 and PowerShell 7+ on Windows, Linux, and macOS.

.PARAMETER Test
    Run the full test suite (unit, SQLite integration, and real-HTTP E2E) instead of starting the API.

.PARAMETER SetupOnly
    Install dependencies, build, and migrate the database, but do not start the API.

.PARAMETER Port
    Local HTTP port for the API. Defaults to 5108.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File .\quickstart.ps1
.EXAMPLE
    pwsh ./quickstart.ps1 -Test
#>
[CmdletBinding()]
param(
    [switch]$Test,
    [switch]$SetupOnly,
    [ValidateRange(1, 65535)]
    [int]$Port = 5108
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$root = $PSScriptRoot
$onWindows = ($PSVersionTable.PSEdition -eq 'Desktop') -or [bool](Get-Variable IsWindows -ValueOnly -ErrorAction SilentlyContinue)
$exe = if ($onWindows) { 'dotnet.exe' } else { 'dotnet' }
$localDotnetDir = Join-Path $root '.dotnet'

function Write-Step([string]$Message) {
    Write-Host "==> $Message" -ForegroundColor Cyan
}

function Invoke-Native([string]$Description, [scriptblock]$Command) {
    Write-Step $Description
    $global:LASTEXITCODE = 0
    & $Command
    if ($LASTEXITCODE -ne 0) {
        throw "$Description failed with exit code $LASTEXITCODE."
    }
}

# Returns true when this dotnet host can resolve the SDK required by global.json.
function Test-RequiredSdk([string]$DotnetPath) {
    if (-not $DotnetPath -or -not (Test-Path -LiteralPath $DotnetPath)) {
        return $false
    }

    $global:LASTEXITCODE = 0
    $version = & $DotnetPath --version 2>$null
    return ($LASTEXITCODE -eq 0) -and [bool]$version
}

function Install-RequiredSdk {
    Write-Step "Installing the .NET SDK from global.json into $localDotnetDir"
    if ($PSVersionTable.PSEdition -eq 'Desktop') {
        [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
    }

    $globalJson = Join-Path $root 'global.json'
    $tempDir = Join-Path ([IO.Path]::GetTempPath()) "metadata-api-dotnet-install-$([Guid]::NewGuid().ToString('N'))"
    New-Item -ItemType Directory -Path $tempDir | Out-Null
    try {
        if ($onWindows) {
            $installer = Join-Path $tempDir 'dotnet-install.ps1'
            Invoke-WebRequest -Uri 'https://dot.net/v1/dotnet-install.ps1' -OutFile $installer -UseBasicParsing
            & $installer -JsonFile $globalJson -InstallDir $localDotnetDir -NoPath
        }
        else {
            $installer = Join-Path $tempDir 'dotnet-install.sh'
            Invoke-WebRequest -Uri 'https://dot.net/v1/dotnet-install.sh' -OutFile $installer
            $global:LASTEXITCODE = 0
            & bash $installer --jsonfile $globalJson --install-dir $localDotnetDir --no-path
            if ($LASTEXITCODE -ne 0) {
                throw ".NET SDK installation failed with exit code $LASTEXITCODE."
            }
        }
    }
    finally {
        Remove-Item -LiteralPath $tempDir -Recurse -Force -ErrorAction SilentlyContinue
    }
}

function Resolve-Dotnet {
    $required = (Get-Content -LiteralPath (Join-Path $root 'global.json') -Raw | ConvertFrom-Json).sdk.version
    Write-Step "Checking for .NET SDK $required (or a newer patch in its feature band)"

    $localDotnet = Join-Path $localDotnetDir $exe
    if (Test-RequiredSdk $localDotnet) {
        return $localDotnet
    }

    $pathDotnet = Get-Command dotnet -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($pathDotnet -and (Test-RequiredSdk $pathDotnet.Source)) {
        return $pathDotnet.Source
    }

    Install-RequiredSdk
    if (-not (Test-RequiredSdk $localDotnet)) {
        throw "The .NET SDK required by global.json could not be installed. Install .NET SDK $required manually from https://dotnet.microsoft.com/download/dotnet/10.0."
    }

    return $localDotnet
}

function New-ApiKey {
    $bytes = New-Object byte[] 32
    $rng = [System.Security.Cryptography.RandomNumberGenerator]::Create()
    try {
        $rng.GetBytes($bytes)
    }
    finally {
        $rng.Dispose()
    }

    return -join ($bytes | ForEach-Object { $_.ToString('x2') })
}

function Assert-PortAvailable([int]$LocalPort) {
    $listener = New-Object System.Net.Sockets.TcpListener ([Net.IPAddress]::Loopback, $LocalPort)
    try {
        $listener.Start()
    }
    catch {
        throw "Port $LocalPort is already in use (perhaps an API instance is still running). Stop that process or rerun with -Port <free port>."
    }
    finally {
        $listener.Stop()
    }
}

Push-Location $root
try {
    $dotnet = Resolve-Dotnet
    if ($dotnet.StartsWith($localDotnetDir, [StringComparison]::OrdinalIgnoreCase)) {
        # Make the repository-local SDK visible to child processes, including dotnet-ef and E2E tests.
        $env:DOTNET_ROOT = $localDotnetDir
        $env:DOTNET_MULTILEVEL_LOOKUP = '0'
        $env:PATH = $localDotnetDir + [IO.Path]::PathSeparator + $env:PATH
    }
    $env:DOTNET_NOLOGO = 'true'
    Write-Host "    Using $dotnet (SDK $(& $dotnet --version))"

    Invoke-Native 'Restoring local tools (dotnet-ef)' { & $dotnet tool restore }

    if ($Test) {
        Invoke-Native 'Running unit, SQLite integration, and real-HTTP E2E tests' {
            & $dotnet test MetadataApi.slnx --configuration Release
        }
        Write-Host 'All tests passed.' -ForegroundColor Green
        return
    }

    Invoke-Native 'Building the solution' { & $dotnet build MetadataApi.slnx }
    Set-Item -LiteralPath 'Env:Logging__LogLevel__Microsoft.EntityFrameworkCore' -Value 'Warning'
    try {
        Invoke-Native 'Applying database migrations' {
            & $dotnet ef database update --project src/MetadataApi --no-build
        }
    }
    finally {
        Remove-Item -LiteralPath 'Env:Logging__LogLevel__Microsoft.EntityFrameworkCore' -ErrorAction SilentlyContinue
    }

    if ($SetupOnly) {
        Write-Host 'Setup complete. Rerun without -SetupOnly to start the API.' -ForegroundColor Green
        return
    }

    Assert-PortAvailable $Port
    if ([string]::IsNullOrWhiteSpace($env:Authentication__ApiKey)) {
        $env:Authentication__ApiKey = New-ApiKey
        $keySource = 'generated for this session'
    }
    else {
        $keySource = 'reused from Authentication__ApiKey'
    }

    $baseUrl = "http://localhost:$Port"
    $key = $env:Authentication__ApiKey
    Write-Host ''
    Write-Host "Metadata API starting at $baseUrl (Ctrl+C to stop)" -ForegroundColor Green
    Write-Host "API key ($keySource): $key"
    Write-Host ''
    Write-Host 'Try it from another PowerShell terminal:' -ForegroundColor Yellow
    Write-Host "  `$headers = @{ Authorization = 'Bearer $key' }"
    Write-Host "  Invoke-RestMethod -Method Post -Uri $baseUrl/api/v1/applications -Headers `$headers -ContentType application/json -Body '{""title"":""Microsoft Garage""}'"
    Write-Host "  Invoke-RestMethod -Uri $baseUrl/api/v1/applications -Headers `$headers"
    Write-Host "  Invoke-RestMethod -Uri $baseUrl/health/ready"
    Write-Host "  OpenAPI document: $baseUrl/openapi/v1.json"
    Write-Host ''

    & $dotnet run --project src/MetadataApi --no-build --launch-profile http -- --urls $baseUrl
}
finally {
    Pop-Location
}
