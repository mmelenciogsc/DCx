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
    dotnet build $solution --configuration Release --no-restore
    dotnet test $solution --configuration Release --no-build --logger "trx;LogFileName=p00-tests.trx" --results-directory $testResults
}
finally {
    Pop-Location
}

