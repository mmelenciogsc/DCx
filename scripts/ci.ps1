[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
$solution = Join-Path $repoRoot "DCx.slnx"
$testResults = Join-Path $repoRoot "artifacts\test-results"

Push-Location $repoRoot
try {
    dotnet --version
    dotnet restore $solution --locked-mode
    if ($LASTEXITCODE -ne 0) { throw "dotnet restore failed with exit code $LASTEXITCODE." }
    dotnet build $solution --configuration Release --no-restore
    if ($LASTEXITCODE -ne 0) { throw "dotnet build failed with exit code $LASTEXITCODE." }
    dotnet test $solution --configuration Release --no-build --logger "trx;LogFileName=p00-tests.trx" --results-directory $testResults
    if ($LASTEXITCODE -ne 0) { throw "dotnet test failed with exit code $LASTEXITCODE." }
}
finally {
    Pop-Location
}
