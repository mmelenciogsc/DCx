[CmdletBinding()]
param(
    [switch]$ExplicitReadOnlyNetworkAudit
)

$ErrorActionPreference = "Stop"
if (-not $ExplicitReadOnlyNetworkAudit) {
    throw "This script performs public-provider HEAD requests. Pass -ExplicitReadOnlyNetworkAudit to continue."
}
if ($PSVersionTable.PSEdition -ne "Core" -or $PSVersionTable.PSVersion.Major -lt 7) {
    throw "The P06 provider-route audit requires PowerShell 7 or later."
}

$repoRoot = Split-Path -Parent $PSScriptRoot
$sourceCommit = (& git.exe -c "safe.directory=$repoRoot" -C $repoRoot rev-parse HEAD 2>$null).Trim()
if ($LASTEXITCODE -ne 0 -or $sourceCommit -notmatch '\A[0-9a-f]{40}\z') {
    throw "The P06 provider-route audit could not resolve the exact source commit."
}
$sourceChanges = @(& git.exe -c "safe.directory=$repoRoot" -C $repoRoot status --porcelain --untracked-files=normal 2>$null)
if ($LASTEXITCODE -ne 0 -or $sourceChanges.Count -ne 0) {
    throw "The P06 provider-route audit requires a clean committed source tree."
}
$coreAssembly = Join-Path $repoRoot "src\DCx.Core\bin\Release\net10.0\DCx.Core.dll"
if (-not (Test-Path -LiteralPath $coreAssembly -PathType Leaf)) {
    throw "Build DCx.Core Release before running the provider-route audit."
}

$runRoot = Join-Path $repoRoot "artifacts\test-results\p06-route-audit-$([Guid]::NewGuid().ToString('N'))"
$evidencePath = Join-Path $runRoot "provider-route-audit.json"
New-Item -ItemType Directory -Path $runRoot -Force | Out-Null

Add-Type -Path $coreAssembly
$catalog = ([DCx.Core.LocalModels.LocalModelDependencyCatalogLoader]::new()).LoadEmbeddedAsync(
    [Threading.CancellationToken]::None).GetAwaiter().GetResult()
$plan = ([DCx.Core.LocalModels.LocalModelDependencyTransferPlanner]::new()).CreateForAll($catalog, $null)

$handler = [Net.Http.SocketsHttpHandler]::new()
$handler.AllowAutoRedirect = $false
$handler.AutomaticDecompression = [Net.DecompressionMethods]::None
$handler.ConnectTimeout = [TimeSpan]::FromSeconds(15)
$handler.Credentials = $null
$handler.MaxResponseHeadersLength = 16
$handler.PreAuthenticate = $false
$handler.UseCookies = $false
$handler.UseProxy = $false
$client = [Net.Http.HttpClient]::new($handler, $true)
$client.Timeout = [TimeSpan]::FromSeconds(25)

function Test-SafeRedirectLocation([Uri]$Location) {
    if ($null -eq $Location -or -not $Location.IsAbsoluteUri -or
        $Location.Scheme -cne [Uri]::UriSchemeHttps -or -not $Location.IsDefaultPort -or
        [Uri]::CheckHostName($Location.IdnHost) -ne [UriHostNameType]::Dns -or
        -not [string]::IsNullOrEmpty($Location.UserInfo) -or
        -not [string]::IsNullOrEmpty($Location.Fragment) -or
        $Location.AbsolutePath.Length -gt 1024 -or $Location.Query.Length -gt 2048 -or
        $Location.AbsoluteUri.Length -gt 4096 -or
        $Location.AbsolutePath.Contains("//", [StringComparison]::Ordinal) -or
        $Location.OriginalString.Contains("\", [StringComparison]::Ordinal)) {
        return $false
    }
    return -not ($Location.OriginalString.ToCharArray() | Where-Object { [char]::IsControl($_) })
}

$results = [Collections.Generic.List[object]]::new()
$failed = $false
$requestCount = 0
$manualRedirects = 0
$allFinalLengthsVerified = $true
try {
    foreach ($item in $plan.Items) {
        $hops = [Collections.Generic.List[object]]::new()
        $visited = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
        [void]$visited.Add($item.SourceUri.AbsoluteUri)
        $currentUri = $item.SourceUri
        $acceptedObservation = $false
        $finalStatusCode = $null
        $finalContentLength = $null
        $finalContentType = $null
        $finalUriWithoutQuery = $null
        $finalUriHadQuery = $false
        $finalQueryCharacters = 0
        $finalLengthVerified = $false
        $redirectCount = 0
        try {
            while ($true) {
                $request = [Net.Http.HttpRequestMessage]::new([Net.Http.HttpMethod]::Head, $currentUri)
                $request.Headers.AcceptEncoding.ParseAdd("identity")
                $request.Headers.CacheControl = [Net.Http.Headers.CacheControlHeaderValue]::new()
                $request.Headers.CacheControl.NoStore = $true
                $response = $null
                try {
                    $requestCount++
                    $response = $client.SendAsync(
                        $request,
                        [Net.Http.HttpCompletionOption]::ResponseHeadersRead,
                        [Threading.CancellationToken]::None).GetAwaiter().GetResult()
                    $statusCode = [int]$response.StatusCode
                    $isRedirect = $statusCode -in @(301, 302, 303, 307, 308)
                    $responseUriMatches = $response.RequestMessage.RequestUri.AbsoluteUri -ceq $currentUri.AbsoluteUri
                    $location = $response.Headers.Location
                    $safeLocation = $null
                    $locationHadQuery = $false
                    $locationQueryCharacters = 0
                    $locationStructurallySafe = $false
                    if ($null -ne $location) {
                        $locationStructurallySafe = Test-SafeRedirectLocation $location
                        $locationHadQuery = -not [string]::IsNullOrEmpty($location.Query)
                        $locationQueryCharacters = $location.Query.Length
                        if ($location.IsAbsoluteUri) {
                            $safeLocation = $location.GetLeftPart([UriPartial]::Authority) + $location.AbsolutePath
                        }
                    }
                    $requestWithoutQuery = $currentUri.GetLeftPart([UriPartial]::Authority) + $currentUri.AbsolutePath
                    $hops.Add([ordered]@{
                        request_uri_without_query = $requestWithoutQuery
                        request_uri_had_query = -not [string]::IsNullOrEmpty($currentUri.Query)
                        request_query_characters = $currentUri.Query.Length
                        status_code = $statusCode
                        response_uri_matches = $responseUriMatches
                        content_length = $response.Content.Headers.ContentLength
                        content_type = $response.Content.Headers.ContentType.MediaType
                        redirect_location_without_query = $safeLocation
                        redirect_location_had_query = $locationHadQuery
                        redirect_query_characters = $locationQueryCharacters
                        redirect_location_structurally_safe = $locationStructurallySafe
                    })

                    if (-not $responseUriMatches) { break }
                    if (-not $isRedirect) {
                        $finalStatusCode = $statusCode
                        $finalContentLength = $response.Content.Headers.ContentLength
                        $finalContentType = $response.Content.Headers.ContentType.MediaType
                        $finalUriWithoutQuery = $requestWithoutQuery
                        $finalUriHadQuery = -not [string]::IsNullOrEmpty($currentUri.Query)
                        $finalQueryCharacters = $currentUri.Query.Length
                        $finalLengthVerified = $null -ne $finalContentLength -and
                            $finalContentLength -eq $item.ExpectedBytes
                        $acceptedObservation = $statusCode -eq 200 -and (
                            $null -eq $finalContentLength -or $finalLengthVerified)
                        break
                    }

                    $redirectCount++
                    $manualRedirects++
                    if ($redirectCount -gt 3 -or -not $locationStructurallySafe -or
                        -not $visited.Add($location.AbsoluteUri)) {
                        break
                    }
                    $currentUri = $location
                }
                finally {
                    if ($null -ne $response) { $response.Dispose() }
                    $request.Dispose()
                }
            }
            if (-not $acceptedObservation) { $failed = $true }
            if (-not $finalLengthVerified) { $allFinalLengthsVerified = $false }
            $results.Add([ordered]@{
                source_uri = $item.SourceUri.AbsoluteUri
                expected_bytes = $item.ExpectedBytes
                sha256 = $item.Sha256
                roles = @($item.Roles)
                disposition = if ($redirectCount -eq 0) { "direct" } else { "redirect" }
                redirect_count = $redirectCount
                final_status_code = $finalStatusCode
                final_content_length = $finalContentLength
                final_content_type = $finalContentType
                final_uri_without_query = $finalUriWithoutQuery
                final_uri_had_query = $finalUriHadQuery
                final_query_characters = $finalQueryCharacters
                final_length_verified = $finalLengthVerified
                accepted_observation = $acceptedObservation
                hops = @($hops)
            })
        }
        catch {
            $failed = $true
            $allFinalLengthsVerified = $false
            $results.Add([ordered]@{
                source_uri = $item.SourceUri.AbsoluteUri
                expected_bytes = $item.ExpectedBytes
                sha256 = $item.Sha256
                roles = @($item.Roles)
                status_code = $null
                disposition = "probe_failed"
                failure_code = "route_probe_failed"
                accepted_observation = $false
                hops = @($hops)
            })
        }
    }
}
finally {
    $client.Dispose()
}

$evidence = [ordered]@{
    schema_version = 1
    captured_at_utc = [DateTimeOffset]::UtcNow.ToString("O")
    source_commit = $sourceCommit
    catalog_id = $catalog.CatalogId
    catalog_sha256 = $catalog.CatalogSha256
    plan_sha256 = $plan.PlanSha256
    request_method = "HEAD"
    plan_item_count = $results.Count
    request_count = $requestCount
    manual_redirects_followed = $manualRedirects
    maximum_redirects_per_item = 3
    response_bodies_read = 0
    automatic_redirects = $false
    proxy_used = $false
    cookies_used = $false
    credentials_used = $false
    authorization_headers_used = $false
    redirect_queries_recorded = $false
    production_policy_changed = $false
    all_observations_accepted = -not $failed
    all_final_lengths_verified = $allFinalLengthsVerified
    observations = @($results)
}
$evidenceJson = $evidence | ConvertTo-Json -Depth 8
if ($evidenceJson.Contains("?", [StringComparison]::Ordinal)) {
    throw "Provider-route evidence unexpectedly contains a query delimiter."
}
$evidenceJson | Set-Content -LiteralPath $evidencePath -Encoding utf8NoBOM

Write-Host "P06 provider route plan items=$($results.Count) HEAD requests=$requestCount manual redirects=$manualRedirects"
Write-Host "P06 provider response bodies read=0"
Write-Host "P06 automatic redirects/proxy/cookies/credentials=0"
foreach ($result in $results) {
    Write-Host "P06 route=$($result.disposition) redirects=$($result.redirect_count) final status=$($result.final_status_code) length verified=$($result.final_length_verified) source=$($result.source_uri) final=$($result.final_uri_without_query)"
}
Write-Host "P06 provider route evidence=$evidencePath"
if ($failed) {
    throw "One or more public-provider route observations failed closed."
}
