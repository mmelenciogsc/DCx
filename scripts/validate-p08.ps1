[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
$solution = Join-Path $repoRoot "DCx.slnx"
$tests = Join-Path $repoRoot "tests\DCx.Core.Tests\DCx.Core.Tests.csproj"

Push-Location $repoRoot
try {
    dotnet restore $solution --locked-mode
    if ($LASTEXITCODE -ne 0) { throw "Locked restore failed with exit code $LASTEXITCODE." }
    dotnet build $solution --configuration Release --no-restore
    if ($LASTEXITCODE -ne 0) { throw "Release build failed with exit code $LASTEXITCODE." }
    dotnet test $tests --configuration Release --no-build --no-restore --filter "FullyQualifiedName~DCx.Core.Tests.Knowledge"
    if ($LASTEXITCODE -ne 0) { throw "P08 knowledge tests failed with exit code $LASTEXITCODE." }

    "Embedded SQLite locked restore=PASS"
    "Private profile isolation=PASS"
    "Offline FTS5 and vector retrieval=PASS"
    "Optimistic version conflict=PASS"
    "Content-addressed artifact integrity=PASS"
    "Hostile CCB ZIP rejection=PASS"
    "Signed CCB provenance and tamper detection=PASS"
    "Bounded CCB harness and BOSS reuse=PASS"
    "P08 private CKB/CCB validation=PASS"
}
finally {
    Pop-Location
}
