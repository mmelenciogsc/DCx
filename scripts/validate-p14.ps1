[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
$solution = Join-Path $repoRoot "DCx.slnx"
$tests = Join-Path $repoRoot "tests\DCx.Core.Tests\DCx.Core.Tests.csproj"
$reporter = Join-Path $repoRoot "tests\DCx.ReadinessReport\bin\Release\net10.0\DCx.ReadinessReport.dll"
$runRoot = Join-Path $repoRoot "artifacts\test-results\p14-$([Guid]::NewGuid().ToString('N'))"
$report = Join-Path $runRoot "go-no-go.json"

Push-Location $repoRoot
try {
    dotnet restore $solution --locked-mode
    if ($LASTEXITCODE -ne 0) { throw "Locked restore failed with exit code $LASTEXITCODE." }
    dotnet build $solution --configuration Release --no-restore
    if ($LASTEXITCODE -ne 0) { throw "Release build failed with exit code $LASTEXITCODE." }
    dotnet test $tests --configuration Release --no-build --no-restore --filter "FullyQualifiedName~ReleaseReadinessTests"
    if ($LASTEXITCODE -ne 0) { throw "P14 readiness tests failed with exit code $LASTEXITCODE." }

    dotnet $reporter $report
    if ($LASTEXITCODE -ne 0) { throw "The readiness reporter failed with exit code $LASTEXITCODE." }
    $assessment = Get-Content -LiteralPath $report -Raw | ConvertFrom-Json
    if ($assessment.decision -ne "no_go" -or $assessment.production_activation_approved -ne $false -or
        $assessment.critical_and_major_issues -ne $assessment.critical_and_major_issues_triaged -or
        $assessment.blocking_critical_and_major_issues -le 0 -or $assessment.pilot_metrics.Count -ne 12) {
        throw "The P14 report did not preserve its NO_GO, triage, blocker, metric or activation gates."
    }

    "P14 readiness tests=PASS"
    "Critical and major issues triaged=$($assessment.critical_and_major_issues_triaged)/$($assessment.critical_and_major_issues)"
    "Blocking critical and major issues=$($assessment.blocking_critical_and_major_issues)"
    "Privacy-minimized pilot metrics=$($assessment.pilot_metrics.Count)"
    "Production activation approved=$($assessment.production_activation_approved)"
    "P14 decision=NO_GO"
    "P14 evidence=$report"
}
finally {
    Pop-Location
}
