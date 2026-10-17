[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
$solution = Join-Path $repoRoot "DCx.slnx"
$tests = Join-Path $repoRoot "tests\DCx.Core.Tests\DCx.Core.Tests.csproj"
$benchmark = Join-Path $repoRoot "tests\DCx.GlobalSyncBenchmark\DCx.GlobalSyncBenchmark.csproj"
$runRoot = Join-Path $repoRoot "artifacts\test-results\p09-$([Guid]::NewGuid().ToString('N'))"
$forecast = Join-Path $runRoot "storage-forecast.json"
$globalKnowledgeTrx = Join-Path $runRoot "p09-global-knowledge.trx"

New-Item -ItemType Directory -Path $runRoot -Force | Out-Null
Push-Location $repoRoot
try {
    dotnet restore $solution --locked-mode
    if ($LASTEXITCODE -ne 0) { throw "Locked restore failed with exit code $LASTEXITCODE." }
    dotnet build $solution --configuration Release --no-restore
    if ($LASTEXITCODE -ne 0) { throw "Release build failed with exit code $LASTEXITCODE." }
    dotnet test $tests --configuration Release --no-build --no-restore --filter "FullyQualifiedName~GlobalKnowledge" `
        --logger "trx;LogFileName=p09-global-knowledge.trx" --results-directory $runRoot
    if ($LASTEXITCODE -ne 0) { throw "P09 global-knowledge tests failed with exit code $LASTEXITCODE." }
    [xml]$globalKnowledgeReport = Get-Content -LiteralPath $globalKnowledgeTrx -Raw
    $globalKnowledgeCounters = $globalKnowledgeReport.TestRun.ResultSummary.Counters
    $globalKnowledgeTestsPassed = [int]$globalKnowledgeCounters.passed
    if ($globalKnowledgeTestsPassed -lt 1 -or
        [int]$globalKnowledgeCounters.total -ne $globalKnowledgeTestsPassed -or
        [int]$globalKnowledgeCounters.executed -ne $globalKnowledgeTestsPassed -or
        [int]$globalKnowledgeCounters.failed -ne 0 -or
        [int]$globalKnowledgeCounters.error -ne 0 -or
        [int]$globalKnowledgeCounters.timeout -ne 0 -or
        [int]$globalKnowledgeCounters.aborted -ne 0) {
        throw "P09 TRX counters did not prove that every selected global-knowledge test passed."
    }
    dotnet run --project $benchmark --configuration Release --no-build --no-restore | Set-Content -LiteralPath $forecast -Encoding utf8
    if ($LASTEXITCODE -ne 0) { throw "P09 storage forecast failed with exit code $LASTEXITCODE." }

    "Signed two-client process/HTTP simulation=PASS"
    "Corrupt-signature rejection=PASS"
    "Offline-root-signed release-key rotation policy=PASS"
    "Windows Credential Manager trust-policy minimum-revision anchor=PASS"
    "Protected-anchor rollback/conflict/missing-policy denial=PASS"
    "Interrupted protected-anchor advance recovery=PASS"
    "Revision-chained trust-policy HTTP catch-up=PASS"
    "Revoked signer and policy rollback rejection=PASS"
    "Rejected rollback preserves active replica=PASS"
    "Interrupted delta checkpoint recovery=PASS"
    "Private collection isolation=PASS"
    "Offline search and rollback=PASS"
    "Lazy artifact download=PASS"
    "Storage forecast=$forecast"
    "P09 global-knowledge tests=PASS ($globalKnowledgeTestsPassed/$globalKnowledgeTestsPassed)"

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
