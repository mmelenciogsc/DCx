[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
$runRoot = Join-Path $repoRoot "artifacts\test-results\p12-$([Guid]::NewGuid().ToString('N'))"
$manifestPath = Join-Path $runRoot "fixture-scenarios.json"
$scenarios = @(
    @{ name = "shared browser login handoff fixture"; script = "validate-p02.ps1" },
    @{ name = "external Windows application handoff fixture"; script = "validate-p03.ps1" },
    @{ name = "document OCR export and local or shared-viewport media frame Q&A fixture"; script = "validate-p07.ps1" },
    @{ name = "private CKB CCB search and tested reuse fixture"; script = "validate-p08.ps1" },
    @{ name = "signed global replica process simulation"; script = "validate-p09.ps1" },
    @{ name = "consent quarantine contribution and receipt fixture"; script = "validate-p10.ps1" },
    @{ name = "durable CARE ledger and payout safety mock"; script = "validate-p11.ps1" },
    @{ name = "local social login post search and QuickBooks XLSX fixtures"; script = "validate-p12-integrations.ps1" },
    @{ name = "exact package installation approval without execution fixture"; script = "validate-p12-package-approval.ps1" }
)
$results = @()

$sourceCommit = (& git -C $repoRoot rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0 -or $sourceCommit -notmatch '^[0-9a-f]{40}$') {
    throw "The P12 gate could not bind evidence to a source commit."
}
$initialTrackedState = @(& git -C $repoRoot status --porcelain --untracked-files=no)
if ($LASTEXITCODE -ne 0 -or $initialTrackedState.Count -ne 0) {
    throw "The P12 aggregate requires a clean tracked worktree."
}

New-Item -ItemType Directory -Path $runRoot -Force | Out-Null
foreach ($scenario in $scenarios) {
    $started = Get-Date
    $path = Join-Path $PSScriptRoot $scenario.script
    & pwsh.exe -NoProfile -ExecutionPolicy Bypass -File $path
    if ($LASTEXITCODE -ne 0) { throw "P12 scenario failed: $($scenario.name)." }
    $results += [ordered]@{
        name = $scenario.name
        validator = $scenario.script
        status = "PASS"
        elapsed_ms = [long]((Get-Date) - $started).TotalMilliseconds
    }
}

$finalSourceCommit = (& git -C $repoRoot rev-parse HEAD).Trim()
$finalTrackedState = @(& git -C $repoRoot status --porcelain --untracked-files=no)
if ($LASTEXITCODE -ne 0 -or $finalSourceCommit -ne $sourceCommit -or $finalTrackedState.Count -ne 0) {
    throw "The P12 source changed while aggregate validation was running."
}

$manifest = [ordered]@{
    captured_at_utc = (Get-Date).ToUniversalTime().ToString("O")
    source_commit = $sourceCommit
    source_tracked_worktree_clean = $true
    fixture_scenarios = $results
    blocked_scenarios = @(
        "live Facebook authenticated search and human acceptance",
        "real QuickBooks sandbox OAuth/API retrieval and formula/table/date/currency conformance",
        "URL download through the inaccessible-PDF wizard to a verified fillable or flattened saved PDF",
        "live YouTube provider playback/frame-Q&A and human acceptance",
        "real signed package installation and clean-VM acceptance",
        "Docker Compose global-service run",
        "real PayPal sandbox subscription webhook and payout reconciliation",
        "human JAWS, NVDA and Narrator acceptance"
    )
    production_actions = 0
}
$manifest | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $manifestPath -Encoding utf8

"P12 fixture scenarios passed=$($results.Count)"
"P12 production actions=0"
"P12 evidence manifest=$manifestPath"
"P12 live and missing-integration gate=BLOCKED"
