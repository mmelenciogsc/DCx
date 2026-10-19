[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$RunRoot
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
$runRoot = [IO.Path]::GetFullPath($RunRoot)
$allowedRoot = [IO.Path]::GetFullPath((Join-Path $repoRoot "artifacts\test-results"))
if (-not $runRoot.StartsWith(
        $allowedRoot + [IO.Path]::DirectorySeparatorChar,
        [StringComparison]::OrdinalIgnoreCase)) {
    throw "The CARE operations loopback UIA root must be beneath test-results."
}

$desktop = Join-Path $repoRoot "src\DCx.Desktop\bin\Release\net10.0-windows10.0.19041.0\DCx.exe"
$globalService = Join-Path $repoRoot "server\DCx.GlobalService\bin\Release\net10.0\DCx.GlobalService.dll"
$seeder = Join-Path $repoRoot "tests\DCx.CareFixtureSeeder\bin\Release\net10.0\DCx.CareFixtureSeeder.dll"
$serverRoot = Join-Path $runRoot "operations-loopback-server"
$signingKeyPath = Join-Path $runRoot "operations-loopback-signing-key.pk8"
$evidencePath = Join-Path $runRoot "operations-loopback-uia.json"
$fixtureSuffix = [Guid]::NewGuid().ToString("N")
$token = "care-operator-loopback-uia-$fixtureSuffix"
$operatorPseudonymBytes = [Text.Encoding]::UTF8.GetBytes(
    "care-operator-loopback-uia-identity-$fixtureSuffix")
$tokenBytes = [Text.Encoding]::UTF8.GetBytes($token)
try {
    $tokenDigest = [Convert]::ToHexString(
        [Security.Cryptography.SHA256]::HashData($tokenBytes)).ToLowerInvariant()
    $operatorPseudonymDigest = [Convert]::ToHexString(
        [Security.Cryptography.SHA256]::HashData($operatorPseudonymBytes)).ToLowerInvariant()
}
finally {
    [Array]::Clear($tokenBytes, 0, $tokenBytes.Length)
    [Array]::Clear($operatorPseudonymBytes, 0, $operatorPseudonymBytes.Length)
}

$serverProcess = $null
$desktopProcess = $null
$deniedProcess = $null
$serverOutputTask = $null
$serverErrorTask = $null
$httpClient = $null

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

function Wait-For([scriptblock]$Condition, [string]$FailureMessage, [int]$Attempts = 100) {
    for ($attempt = 0; $attempt -lt $Attempts; $attempt++) {
        $result = & $Condition
        if ($result) { return $result }
        Start-Sleep -Milliseconds 200
    }
    throw $FailureMessage
}

function Find-ById($Root, [string]$AutomationId) {
    $condition = [System.Windows.Automation.PropertyCondition]::new(
        [System.Windows.Automation.AutomationElement]::AutomationIdProperty,
        $AutomationId)
    return $Root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)
}

function Find-WindowByProcessAndId([int]$ProcessId, [string]$AutomationId) {
    $elements = [System.Windows.Automation.AutomationElement]::RootElement.FindAll(
        [System.Windows.Automation.TreeScope]::Descendants,
        [System.Windows.Automation.Condition]::TrueCondition)
    return @($elements | Where-Object {
        $_.Current.ProcessId -eq $ProcessId -and $_.Current.AutomationId -eq $AutomationId
    }) | Select-Object -First 1
}

function Get-Value($Root, [string]$AutomationId) {
    $element = Wait-For {
        Find-ById $Root $AutomationId
    } "The $AutomationId value was not exposed."
    return $element.GetCurrentPattern(
        [System.Windows.Automation.ValuePattern]::Pattern).Current.Value
}

function Invoke-ById($Root, [string]$AutomationId) {
    $element = Wait-For {
        Find-ById $Root $AutomationId
    } "The $AutomationId action was not exposed."
    $element.GetCurrentPattern(
        [System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    return $element
}

function Reserve-LoopbackPort {
    $listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
    try {
        $listener.Start()
        return ([Net.IPEndPoint]$listener.LocalEndpoint).Port
    }
    finally {
        $listener.Stop()
    }
}

function Stop-OwnedProcess($Process) {
    if ($null -eq $Process -or $Process.HasExited) { return }
    $null = $Process.CloseMainWindow()
    if (-not $Process.WaitForExit(3000)) {
        $Process.Kill($true)
        $Process.WaitForExit()
    }
}

if (-not (Test-Path -LiteralPath $desktop -PathType Leaf) -or
    -not (Test-Path -LiteralPath $globalService -PathType Leaf) -or
    -not (Test-Path -LiteralPath $seeder -PathType Leaf)) {
    throw "Build DCx Desktop, GlobalService, and CareFixtureSeeder before loopback UIA validation."
}

New-Item -ItemType Directory -Path $runRoot -Force | Out-Null

try {
    $deniedStart = [Diagnostics.ProcessStartInfo]::new($desktop)
    $deniedStart.UseShellExecute = $false
    $deniedStart.CreateNoWindow = $true
    $deniedStart.ArgumentList.Add("--care-operations-loopback-fixture")
    $null = $deniedStart.Environment.Remove("DCX_CARE_OPERATIONS_LOOPBACK_FIXTURE")
    $null = $deniedStart.Environment.Remove("DCX_CARE_OPERATIONS_LOOPBACK_FIXTURE_ORIGIN")
    $null = $deniedStart.Environment.Remove("DCX_CARE_OPERATIONS_LOOPBACK_FIXTURE_TOKEN")
    $deniedProcess = [Diagnostics.Process]::Start($deniedStart)
    if (-not $deniedProcess.WaitForExit(5000) -or $deniedProcess.ExitCode -ne 2) {
        throw "The CARE operations loopback desktop fixture did not fail closed when ungated."
    }
    if ($null -ne (Find-WindowByProcessAndId $deniedProcess.Id "CareOperationsWindow")) {
        throw "The ungated CARE operations loopback fixture exposed a window."
    }

    $ecdsa = [Security.Cryptography.ECDsa]::Create(
        [Security.Cryptography.ECCurve+NamedCurves]::nistP256)
    try {
        $keyBytes = $ecdsa.ExportPkcs8PrivateKey()
        try {
            [IO.File]::WriteAllBytes($signingKeyPath, $keyBytes)
        }
        finally {
            [Array]::Clear($keyBytes, 0, $keyBytes.Length)
        }
    }
    finally {
        $ecdsa.Dispose()
    }

    $dotnet = (Get-Command dotnet -ErrorAction Stop).Source
    $seedOutput = & $dotnet $seeder $serverRoot
    if ($LASTEXITCODE -ne 0) {
        throw "The CARE operations validation ledger seeder failed."
    }
    $seed = $seedOutput | Select-Object -Last 1 | ConvertFrom-Json
    if ($seed.schemaVersion -ne 1 -or $seed.CarePeriodId -cne "care:2026-05" -or
        $seed.AmountCents -ne 700 -or $seed.FailureCode -cne "provider_rejected" -or
        $seed.HoldCode -cne "failed_item_hold" -or $seed.NoticeCode -cne "not_ready" -or
        $seed.DispositionCode -cne "unreviewed" -or $seed.providerRequests -ne 0 -or
        $seed.payoutAttempts -ne 0 -or $seed.notificationTransports -ne 0 -or
        $seed.productionActions -ne 0) {
        throw "The CARE operations validation ledger seed exceeded its synthetic boundary."
    }

    $port = Reserve-LoopbackPort
    $origin = "http://127.0.0.1:$port/"
    $serverStart = [Diagnostics.ProcessStartInfo]::new($dotnet)
    $serverStart.UseShellExecute = $false
    $serverStart.CreateNoWindow = $true
    $serverStart.RedirectStandardOutput = $true
    $serverStart.RedirectStandardError = $true
    $serverStart.ArgumentList.Add($globalService)
    $serverStart.Environment["DCX_GLOBAL_SIMULATION"] = "1"
    $serverStart.Environment["DCX_GLOBAL_DATA_ROOT"] = $serverRoot
    $serverStart.Environment["DCX_GLOBAL_SIGNING_KEY"] = $signingKeyPath
    $serverStart.Environment["DCX_GLOBAL_SIMULATION_URL"] = $origin
    $serverStart.Environment["DCX_GLOBAL_CARE_OPERATIONS_API"] = "1"
    $serverStart.Environment["DCX_GLOBAL_CARE_OPERATOR_TOKEN_SHA256"] = $tokenDigest
    $serverStart.Environment["DCX_GLOBAL_CARE_OPERATOR_PSEUDONYM_SHA256"] =
        $operatorPseudonymDigest
    $serverStart.Environment["Logging__LogLevel__Default"] = "Warning"
    $serverProcess = [Diagnostics.Process]::Start($serverStart)
    $serverOutputTask = $serverProcess.StandardOutput.ReadToEndAsync()
    $serverErrorTask = $serverProcess.StandardError.ReadToEndAsync()

    $httpClient = [Net.Http.HttpClient]::new()
    $httpClient.Timeout = [TimeSpan]::FromSeconds(2)
    $null = Wait-For {
        if ($serverProcess.HasExited) {
            throw "The CARE operations loopback server exited before becoming healthy."
        }
        try {
            $response = $httpClient.GetAsync($origin + "health").GetAwaiter().GetResult()
            try {
                if (-not $response.IsSuccessStatusCode) { return $null }
                $health = $response.Content.ReadAsStringAsync().GetAwaiter().GetResult() |
                    ConvertFrom-Json
                if ($health.careOperationsApi -eq $true -and
                    $health.careOperationsProcessing -eq $false -and
                    $health.careOperationsRateLimit -eq $true -and
                    $health.careContributorDashboardApi -eq $false) { return $health }
            }
            finally {
                $response.Dispose()
            }
        }
        catch [Net.Http.HttpRequestException] {
        }
        catch [Threading.Tasks.TaskCanceledException] {
        }
        return $null
    } "The CARE operations loopback server did not expose its exact disabled-processing state."

    $desktopStart = [Diagnostics.ProcessStartInfo]::new($desktop)
    $desktopStart.UseShellExecute = $false
    $desktopStart.CreateNoWindow = $true
    $desktopStart.ArgumentList.Add("--care-operations-loopback-fixture")
    $desktopStart.Environment["DCX_CARE_OPERATIONS_LOOPBACK_FIXTURE"] = "1"
    $desktopStart.Environment["DCX_CARE_OPERATIONS_LOOPBACK_FIXTURE_ORIGIN"] = $origin
    $desktopStart.Environment["DCX_CARE_OPERATIONS_LOOPBACK_FIXTURE_TOKEN"] = $token
    $desktopProcess = [Diagnostics.Process]::Start($desktopStart)
    $null = $desktopStart.Environment.Remove("DCX_CARE_OPERATIONS_LOOPBACK_FIXTURE_TOKEN")

    $window = Wait-For {
        Find-WindowByProcessAndId $desktopProcess.Id "CareOperationsWindow"
    } "The authenticated CARE operations loopback window did not open."
    $heading = Wait-For {
        Find-ById $window "CareOperationsHeading"
    } "The CARE operations loopback heading was not exposed."
    $null = Wait-For {
        if ($heading.Current.HasKeyboardFocus) { return $true }
    } "Keyboard focus did not enter the CARE operations loopback heading."
    $boundary = Wait-For {
        $element = Find-ById $window "CareOperationsBoundary"
        if ($null -ne $element -and $element.Current.Name -like
            "Default-off authenticated local validation transport*integrity-verified synthetic CARE ledger*not production authentication*PayPal requests*production actions are unavailable.*") {
            return $element
        }
    } "The authenticated CARE operations fixture boundary was not exposed."
    $summary = Wait-For {
        $value = Get-Value $window "CareOperationsSummary"
        if ($value -like "*Status: clear.*" -and $value -like "*Pending: 0.*" -and
            $value -like "*Local processing available: no.*" -and
            $value -like "*Data source: authenticated local CARE ledger fixture.*") {
            return $value
        }
    } "The authenticated CARE ledger aggregate summary was not exposed."

    Invoke-ById $window "LoadFailedPayoutReviewsButton" | Out-Null
    $failedList = Wait-For {
        $element = Find-ById $window "FailedPayoutReviewsList"
        if ($null -ne $element) {
            $items = $element.FindAll(
                [System.Windows.Automation.TreeScope]::Children,
                [System.Windows.Automation.Condition]::TrueCondition)
            if ($items.Count -eq 1) { return $element }
        }
    } "The durable privacy-minimized failed payout hold was not exposed."
    $failedItems = $failedList.FindAll(
        [System.Windows.Automation.TreeScope]::Children,
        [System.Windows.Automation.Condition]::TrueCondition)
    $failedItems[0].GetCurrentPattern(
        [System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
    $detailsBefore = Wait-For {
        $value = Get-Value $window "FailedPayoutReviewDetails"
        if ($value -like "*CARE period: care:2026-05.*" -and
            $value -like "*Amount: USD 7.00.*" -and
            $value -like "*Closed failure code: provider_rejected.*" -and
            $value -like "*Hold code: failed_item_hold.*" -and
            $value -like "*Local notice code: not_ready.*" -and
            $value -like "*Current disposition: unreviewed.*" -and
            $value -notlike "*contributor:*" -and $value -notlike "*@*" -and
            $value -notlike "*destination*" -and $value -notlike "*snapshot:*") {
            return $value
        }
    } "The durable failed-payout hold was not privacy-minimized in the native UI."

    $approval = Find-ById $window "ApproveFailedPayoutDispositionBox"
    $approvalToggle = $approval.GetCurrentPattern(
        [System.Windows.Automation.TogglePattern]::Pattern)
    if ($approvalToggle.Current.ToggleState -ne
        [System.Windows.Automation.ToggleState]::Off) {
        throw "The durable failed-payout decision approval did not default to unchecked."
    }
    $requestUpdate = Find-ById $window "RequestFailedPayoutRecipientUpdateOption"
    $requestUpdate.GetCurrentPattern(
        [System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
    Invoke-ById $window "RecordFailedPayoutDispositionButton" | Out-Null
    $null = Wait-For {
        $status = Find-ById $window "CareOperationsStatus"
        if ($null -ne $status -and $status.Current.Name -like
            "No failed-payout decision was recorded.*explicitly approve it first.*") {
            return $status
        }
    } "The authenticated disposition without approval did not fail closed."
    if (-not $approval.Current.HasKeyboardFocus -or
        $approvalToggle.Current.ToggleState -ne [System.Windows.Automation.ToggleState]::Off) {
        throw "Denied authenticated disposition did not reset and focus explicit approval."
    }

    $requestUpdate.GetCurrentPattern(
        [System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
    $approvalToggle.Toggle()
    Invoke-ById $window "RecordFailedPayoutDispositionButton" | Out-Null
    $statusAfter = Wait-For {
        $status = Find-ById $window "CareOperationsStatus"
        if ($null -ne $status -and $status.Current.Name -eq
            "Failed-payout decision recorded as request_recipient_update. Local dashboard notice local_dashboard_notice_ready. Provider calls 0. Financial mutations 0. Payout attempts 0. Notification transports 0. No retry was sent and the payout remains held.") {
            return $status
        }
    } "The authenticated zero-authority failed-payout disposition was not announced."
    $detailsAfter = Wait-For {
        $element = Find-ById $window "FailedPayoutReviewDetails"
        $value = $element.GetCurrentPattern(
            [System.Windows.Automation.ValuePattern]::Pattern).Current.Value
        if ($value -like "*Local notice code: local_dashboard_notice_ready.*" -and
            $value -like "*Current disposition: request_recipient_update.*" -and
            $element.Current.HasKeyboardFocus) { return $value }
    } "The authenticated durable decision or detail focus was not exposed."
    if ($approvalToggle.Current.ToggleState -ne [System.Windows.Automation.ToggleState]::Off -or
        (Find-ById $window "RecordFailedPayoutDispositionButton").Current.IsEnabled) {
        throw "The durable failed-payout approval did not reset or replay remained enabled."
    }

    $request = [Net.Http.HttpRequestMessage]::new(
        [Net.Http.HttpMethod]::Get,
        $origin + "api/care/operations/failed-payout-holds")
    $request.Headers.Authorization = [Net.Http.Headers.AuthenticationHeaderValue]::new(
        "Bearer", $token)
    $response = $httpClient.SendAsync($request).GetAwaiter().GetResult()
    try {
        if (-not $response.IsSuccessStatusCode -or -not $response.Headers.CacheControl.NoStore) {
            throw "The authenticated post-decision projection was unavailable or cacheable."
        }
        $rawProjection = $response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
        if ($rawProjection -like "*contributor:*" -or $rawProjection -like "*@*" -or
            $rawProjection -like "*snapshot:*" -or $rawProjection -like "*batch:*") {
            throw "The authenticated failed-payout projection disclosed a forbidden identifier."
        }
        $projection = @($rawProjection | ConvertFrom-Json)
        if ($projection.Count -ne 1 -or
            $projection[0].dispositionCode -cne "request_recipient_update" -or
            $projection[0].noticeCode -cne "local_dashboard_notice_ready") {
            throw "The durable failed-payout disposition did not survive an authenticated reread."
        }
    }
    finally {
        $response.Dispose()
        $request.Dispose()
    }

    Remove-Item -LiteralPath $signingKeyPath -Force
    if (Test-Path -LiteralPath $signingKeyPath) {
        throw "The ephemeral CARE operations fixture signing key was retained."
    }

    [ordered]@{
        schema_version = 1
        source = "authenticated_loopback_synthetic_care_ledger"
        startup_gate_default_off = $true
        processing_capability = $false
        heading_focus = $true
        privacy_minimized_review_count = 1
        review_reference_length = ([string]$seed.ReviewId).Length
        explicit_approval_default_off = $true
        denied_attempt_focus_return = $true
        disposition = "request_recipient_update"
        local_notice = "local_dashboard_notice_ready"
        replay_enabled_after_decision = $false
        provider_requests = 0
        disposition_financial_mutations = 0
        payout_attempts = 0
        notification_transports = 0
        production_actions = 0
        ephemeral_signing_key_retained = $false
        boundary = [string]$boundary.Current.Name
        summary = $summary
        details_before = $detailsBefore
        details_after = $detailsAfter
        status_after = [string]$statusAfter.Current.Name
    } | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $evidencePath -Encoding utf8
}
finally {
    Stop-OwnedProcess $desktopProcess
    Stop-OwnedProcess $deniedProcess
    Stop-OwnedProcess $serverProcess
    if (Test-Path -LiteralPath $signingKeyPath) {
        Remove-Item -LiteralPath $signingKeyPath -Force
    }
    if ($null -ne $serverOutputTask -and $null -ne $serverErrorTask) {
        $diagnostics = $serverOutputTask.GetAwaiter().GetResult() +
            $serverErrorTask.GetAwaiter().GetResult()
        if ($diagnostics.Contains($token, [StringComparison]::Ordinal) -or
            $diagnostics.Contains("contributor:fixture", [StringComparison]::Ordinal) -or
            $diagnostics.Contains("personal.example", [StringComparison]::Ordinal)) {
            throw "CARE operations loopback diagnostics disclosed a token or source identity."
        }
    }
    if ($null -ne $httpClient) { $httpClient.Dispose() }
}
