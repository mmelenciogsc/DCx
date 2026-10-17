[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
$runRoot = Join-Path $repoRoot "artifacts\test-results\p12-$([Guid]::NewGuid().ToString('N'))"
$manifestPath = Join-Path $runRoot "fixture-scenarios.json"
$scenarios = @(
    @{ name = "shared browser login handoff fixture"; script = "validate-p02.ps1" },
    @{ name = "external Windows application handoff fixture"; script = "validate-p03.ps1" },
    @{ name = "document OCR export local media and YouTube handoff fixture"; script = "validate-p07.ps1" },
    @{ name = "private CKB CCB search and tested reuse fixture"; script = "validate-p08.ps1" },
    @{ name = "signed global replica process simulation"; script = "validate-p09.ps1" },
    @{ name = "consent quarantine contribution and receipt fixture"; script = "validate-p10.ps1" },
    @{ name = "CARE ledger and payout safety mock"; script = "validate-p11.ps1" }
)
$results = @()

New-Item -ItemType Directory -Path $runRoot -Force | Out-Null
foreach ($scenario in $scenarios) {
    $started = Get-Date
    $path = Join-Path $PSScriptRoot $scenario.script
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $path
    if ($LASTEXITCODE -ne 0) { throw "P12 scenario failed: $($scenario.name)." }
    $results += [ordered]@{
        name = $scenario.name
        validator = $scenario.script
        status = "PASS"
        elapsed_ms = [long]((Get-Date) - $started).TotalMilliseconds
    }
}

$manifest = [ordered]@{
    captured_at_utc = (Get-Date).ToUniversalTime().ToString("O")
    fixture_scenarios = $results
    blocked_scenarios = @(
        "Facebook-specific authenticated search and human acceptance",
        "QuickBooks sandbox OAuth to XLSX",
        "YouTube frame capture and Q&A beyond safe browser handoff",
        "approved package installer execution",
        "Docker Compose global-service run",
        "real PayPal sandbox subscription webhook and payout reconciliation"
    )
    production_actions = 0
}
$manifest | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $manifestPath -Encoding utf8

"P12 fixture scenarios passed=$($results.Count)"
"P12 production actions=0"
"P12 evidence manifest=$manifestPath"
"P12 live and missing-integration gate=BLOCKED"
