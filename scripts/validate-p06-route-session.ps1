[CmdletBinding()]
param(
    [switch]$ExplicitReadOnlyNetworkReview
)

$ErrorActionPreference = "Stop"
if (-not $ExplicitReadOnlyNetworkReview) {
    throw "This script invokes the product's public-provider HEAD-only route review. Pass -ExplicitReadOnlyNetworkReview to continue."
}
if ($PSVersionTable.PSEdition -ne "Core" -or $PSVersionTable.PSVersion.Major -lt 7) {
    throw "The P06 product route-session validation requires PowerShell 7 or later."
}

$repoRoot = Split-Path -Parent $PSScriptRoot
$sourceCommit = (& git.exe -c "safe.directory=$repoRoot" -C $repoRoot rev-parse HEAD 2>$null).Trim()
if ($LASTEXITCODE -ne 0 -or $sourceCommit -notmatch '\A[0-9a-f]{40}\z') {
    throw "The P06 product route-session validation could not resolve the exact source commit."
}
$sourceChanges = @(& git.exe -c "safe.directory=$repoRoot" -C $repoRoot status --porcelain --untracked-files=normal 2>$null)
if ($LASTEXITCODE -ne 0 -or $sourceChanges.Count -ne 0) {
    throw "The P06 product route-session validation requires a clean committed source tree."
}

$coreAssembly = Join-Path $repoRoot "src\DCx.Core\bin\Release\net10.0\DCx.Core.dll"
if (-not (Test-Path -LiteralPath $coreAssembly -PathType Leaf)) {
    throw "Build DCx.Core Release before running the product route-session validation."
}

$runRoot = Join-Path $repoRoot "artifacts\test-results\p06-route-session-$([Guid]::NewGuid().ToString('N'))"
$evidencePath = Join-Path $runRoot "product-route-session.json"
New-Item -ItemType Directory -Path $runRoot -Force | Out-Null

Add-Type -Path $coreAssembly
$catalog = ([DCx.Core.LocalModels.LocalModelDependencyCatalogLoader]::new()).LoadEmbeddedAsync(
    [Threading.CancellationToken]::None).GetAwaiter().GetResult()
$plan = ([DCx.Core.LocalModels.LocalModelDependencyTransferPlanner]::new()).CreateForAll($catalog, $null)
$approval = [DCx.Core.LocalModels.LocalModelDependencyRedirectReviewApproval]::Create($plan, $true)
$reviewer = [DCx.Core.LocalModels.LocalModelDependencyRedirectReviewer]::new()
try {
    $session = $reviewer.ReviewEmbeddedAsync(
        $plan,
        $approval,
        [Threading.CancellationToken]::None).GetAwaiter().GetResult()
}
finally {
    $reviewer.Dispose()
}

if (-not $session.IsCurrent -or $session.PlanSha256 -cne $plan.PlanSha256 -or
    $session.ItemCount -ne 9 -or $session.RequestCount -ne 12 -or
    $session.RedirectCount -ne 3 -or $session.ExactLengthCount -ne 8 -or
    $session.PolicyReview.SchemaVersion -ne 2 -or
    -not $session.PolicyReview.AllowsRedirects -or $session.PolicyReview.Routes.Count -ne 3) {
    throw "The product route-review session did not match the exact current plan and observed route contract."
}

$routes = foreach ($route in $session.PolicyReview.Routes) {
    if (-not [string]::IsNullOrEmpty($route.FromUriWithoutQuery.Query) -or
        -not [string]::IsNullOrEmpty($route.TargetUriWithoutQuery.Query)) {
        throw "The product route-review session retained a redirect query value."
    }
    [ordered]@{
        item_source_uri = $route.ItemSourceUri.AbsoluteUri
        expected_bytes = $route.ExpectedBytes
        sha256 = $route.Sha256
        from_uri_without_query = $route.FromUriWithoutQuery.AbsoluteUri
        target_uri_without_query = $route.TargetUriWithoutQuery.AbsoluteUri
    }
}

$evidence = [ordered]@{
    schema_version = 1
    captured_at_utc = [DateTimeOffset]::UtcNow.ToString("O")
    source_commit = $sourceCommit
    catalog_id = $catalog.CatalogId
    catalog_sha256 = $catalog.CatalogSha256
    plan_sha256 = $plan.PlanSha256
    policy_schema_version = $session.PolicyReview.SchemaVersion
    policy_sha256 = $session.PolicyReview.PolicySha256
    observed_at_utc = $session.ObservedAtUtc.ToString("O")
    expires_at_utc = $session.ExpiresAtUtc.ToString("O")
    session_persisted = $false
    request_method = "HEAD"
    item_count = $session.ItemCount
    request_count = $session.RequestCount
    redirect_count = $session.RedirectCount
    exact_final_length_count = $session.ExactLengthCount
    response_bodies_read = 0
    signed_query_values_retained = $false
    payload_get_requests = 0
    payload_bytes_read = 0
    transfer_approval_created = $false
    coordinator_created = $false
    production_transfer_started = $false
    routes = @($routes)
}
$evidenceJson = $evidence | ConvertTo-Json -Depth 6
if ($evidenceJson.Contains("?", [StringComparison]::Ordinal)) {
    throw "Product route-session evidence unexpectedly contains a query delimiter."
}
$evidenceJson | Set-Content -LiteralPath $evidencePath -Encoding utf8NoBOM

"P06 product route session=PASS"
"P06 exact plan items=$($session.ItemCount) HEAD requests=$($session.RequestCount) redirects=$($session.RedirectCount) exact lengths=$($session.ExactLengthCount)"
"P06 policy schema=$($session.PolicyReview.SchemaVersion) routes=$($session.PolicyReview.Routes.Count) expires=$($session.ExpiresAtUtc.ToString('O'))"
"P06 response bodies/payload GETs/payload bytes/transfers=0"
"P06 product route-session evidence=$evidencePath"
