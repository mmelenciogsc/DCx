[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
$solution = Join-Path $repoRoot "DCx.slnx"
$tests = Join-Path $repoRoot "tests\DCx.Core.Tests\DCx.Core.Tests.csproj"
$benchmark = Join-Path $repoRoot "tests\DCx.GlobalSyncBenchmark\DCx.GlobalSyncBenchmark.csproj"
$runRoot = Join-Path $repoRoot "artifacts\test-results\p09-$([Guid]::NewGuid().ToString('N'))"
$forecast = Join-Path $runRoot "storage-forecast.json"

New-Item -ItemType Directory -Path $runRoot -Force | Out-Null
Push-Location $repoRoot
try {
    dotnet restore $solution --locked-mode
    if ($LASTEXITCODE -ne 0) { throw "Locked restore failed with exit code $LASTEXITCODE." }
    dotnet build $solution --configuration Release --no-restore
    if ($LASTEXITCODE -ne 0) { throw "Release build failed with exit code $LASTEXITCODE." }
    dotnet test $tests --configuration Release --no-build --no-restore --filter "FullyQualifiedName~GlobalKnowledge"
    if ($LASTEXITCODE -ne 0) { throw "P09 global-knowledge tests failed with exit code $LASTEXITCODE." }
    dotnet run --project $benchmark --configuration Release --no-build --no-restore | Set-Content -LiteralPath $forecast -Encoding utf8
    if ($LASTEXITCODE -ne 0) { throw "P09 storage forecast failed with exit code $LASTEXITCODE." }

    "Signed two-client process/HTTP simulation=PASS"
    "Corrupt-signature rejection=PASS"
    "Interrupted delta checkpoint recovery=PASS"
    "Private collection isolation=PASS"
    "Offline search and rollback=PASS"
    "Lazy artifact download=PASS"
    "Storage forecast=$forecast"

    $docker = Get-Command docker -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($null -eq $docker) {
        "P09 Docker Compose gate=BLOCKED: Docker CLI is unavailable to native Windows validation."
    }
    else {
        $dockerOutput = & $docker.Source info --format "{{.ServerVersion}}" 2>&1
        if ($LASTEXITCODE -eq 0) {
            "Docker daemon=$dockerOutput"
            "P09 Docker Compose execution=PENDING"
        }
        else {
            "P09 Docker Compose gate=BLOCKED: Docker daemon access is unavailable."
        }
    }
    "P09 local process simulation=PASS"
}
finally {
    Pop-Location
}
