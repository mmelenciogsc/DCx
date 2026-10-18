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
    throw "The CARE contributor loopback UIA run root must be beneath test-results."
}

$desktop = Join-Path $repoRoot "src\DCx.Desktop\bin\Release\net10.0-windows10.0.19041.0\DCx.exe"
$globalService = Join-Path $repoRoot "server\DCx.GlobalService\bin\Release\net10.0\DCx.GlobalService.dll"
$serverRoot = Join-Path $runRoot "contributor-loopback-server"
$signingKeyPath = Join-Path $runRoot "contributor-loopback-signing-key.pk8"
$evidencePath = Join-Path $runRoot "contributor-loopback-uia.json"
$fixtureSuffix = [Guid]::NewGuid().ToString("N")
$contributorPseudonym = "contributor:loopback-uia-$fixtureSuffix"
$token = "care-contributor-loopback-uia-$fixtureSuffix"
$tokenBytes = [Text.Encoding]::UTF8.GetBytes($token)
try {
    $tokenDigest = [Convert]::ToHexString(
        [Security.Cryptography.SHA256]::HashData($tokenBytes)).ToLowerInvariant()
}
finally {
    [Array]::Clear($tokenBytes, 0, $tokenBytes.Length)
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
    $element = Wait-For { Find-ById $Root $AutomationId } "The $AutomationId value was not exposed."
    return $element.GetCurrentPattern(
        [System.Windows.Automation.ValuePattern]::Pattern).Current.Value
}

function Invoke-ById($Root, [string]$AutomationId) {
    $element = Wait-For { Find-ById $Root $AutomationId } "The $AutomationId action was not exposed."
    $element.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
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
    -not (Test-Path -LiteralPath $globalService -PathType Leaf)) {
    throw "Build DCx Desktop and GlobalService before loopback UIA validation."
}

New-Item -ItemType Directory -Path $runRoot -Force | Out-Null
New-Item -ItemType Directory -Path $serverRoot -Force | Out-Null

try {
    $deniedStart = [Diagnostics.ProcessStartInfo]::new($desktop)
    $deniedStart.UseShellExecute = $false
    $deniedStart.CreateNoWindow = $true
    $deniedStart.ArgumentList.Add("--care-contributor-loopback-fixture")
    $null = $deniedStart.Environment.Remove("DCX_CARE_CONTRIBUTOR_LOOPBACK_FIXTURE")
    $null = $deniedStart.Environment.Remove("DCX_CARE_CONTRIBUTOR_LOOPBACK_FIXTURE_ORIGIN")
    $null = $deniedStart.Environment.Remove("DCX_CARE_CONTRIBUTOR_LOOPBACK_FIXTURE_TOKEN")
    $null = $deniedStart.Environment.Remove("DCX_CARE_CONTRIBUTOR_LOOPBACK_FIXTURE_PSEUDONYM")
    $deniedProcess = [Diagnostics.Process]::Start($deniedStart)
    if (-not $deniedProcess.WaitForExit(5000) -or $deniedProcess.ExitCode -ne 2) {
        throw "The contributor loopback desktop fixture did not fail closed when ungated."
    }
    if ($null -ne (Find-WindowByProcessAndId $deniedProcess.Id "CareContributorDashboardWindow")) {
        throw "The ungated contributor loopback fixture exposed a dashboard window."
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

    $port = Reserve-LoopbackPort
    $origin = "http://127.0.0.1:$port/"
    $dotnet = (Get-Command dotnet -ErrorAction Stop).Source
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
    $serverStart.Environment["DCX_GLOBAL_CARE_CONTRIBUTOR_DASHBOARD_API"] = "1"
    $serverStart.Environment["DCX_GLOBAL_CARE_CONTRIBUTOR_TOKEN_SHA256"] = $tokenDigest
    $serverStart.Environment["DCX_GLOBAL_CARE_CONTRIBUTOR_PSEUDONYM"] = $contributorPseudonym
    $serverStart.Environment["Logging__LogLevel__Default"] = "Warning"
    $serverProcess = [Diagnostics.Process]::Start($serverStart)
    $serverOutputTask = $serverProcess.StandardOutput.ReadToEndAsync()
    $serverErrorTask = $serverProcess.StandardError.ReadToEndAsync()

    $httpClient = [Net.Http.HttpClient]::new()
    $httpClient.Timeout = [TimeSpan]::FromSeconds(2)
    $health = Wait-For {
        if ($serverProcess.HasExited) {
            throw "The contributor loopback server exited before becoming healthy."
        }
        try {
            $response = $httpClient.GetAsync($origin + "health").GetAwaiter().GetResult()
            try {
                if (-not $response.IsSuccessStatusCode) { return $null }
                $json = $response.Content.ReadAsStringAsync().GetAwaiter().GetResult() |
                    ConvertFrom-Json
                if ($json.careContributorDashboardApi -eq $true -and
                    $json.careContributorDashboardRateLimit -eq $true -and
                    $json.careOperationsApi -eq $false) { return $json }
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
    } "The contributor loopback server did not expose its exact read-only health state."

    $desktopStart = [Diagnostics.ProcessStartInfo]::new($desktop)
    $desktopStart.UseShellExecute = $false
    $desktopStart.CreateNoWindow = $true
    $desktopStart.ArgumentList.Add("--care-contributor-loopback-fixture")
    $desktopStart.Environment["DCX_CARE_CONTRIBUTOR_LOOPBACK_FIXTURE"] = "1"
    $desktopStart.Environment["DCX_CARE_CONTRIBUTOR_LOOPBACK_FIXTURE_ORIGIN"] = $origin
    $desktopStart.Environment["DCX_CARE_CONTRIBUTOR_LOOPBACK_FIXTURE_TOKEN"] = $token
    $desktopStart.Environment["DCX_CARE_CONTRIBUTOR_LOOPBACK_FIXTURE_PSEUDONYM"] =
        $contributorPseudonym
    $desktopProcess = [Diagnostics.Process]::Start($desktopStart)
    $null = $desktopStart.Environment.Remove("DCX_CARE_CONTRIBUTOR_LOOPBACK_FIXTURE_TOKEN")

    $window = Wait-For {
        Find-WindowByProcessAndId $desktopProcess.Id "CareContributorDashboardWindow"
    } "The authenticated contributor loopback dashboard did not open."
    $heading = Wait-For {
        Find-ById $window "CareContributorDashboardHeading"
    } "The contributor loopback dashboard heading was not exposed."
    $null = Wait-For {
        if ($heading.Current.HasKeyboardFocus) { return $true }
    } "Keyboard focus did not enter the contributor loopback heading."
    $boundary = Wait-For {
        $element = Find-ById $window "CareContributorDashboardBoundary"
        if ($null -ne $element -and $element.Current.Name -like
            "Default-off authenticated local validation transport*integrity-verified local CARE ledger*not production authentication*PayPal requests*production actions are unavailable.*") {
            return $element
        }
    } "The contributor loopback validation and production-auth boundary was not exposed."
    $summary = Wait-For {
        $value = Get-Value $window "CareContributorDashboardSummary"
        if ($value -like "*Contributor pseudonym: $contributorPseudonym.*" -and
            $value -like "*Periods: 0.*" -and
            $value -like "*Allocated: USD 0.00.*" -and
            $value -like "*Paid: USD 0.00.*" -and
            $value -like "*Outstanding or held: USD 0.00.*" -and
            $value -like "*Data source: authenticated local CARE ledger fixture.*") {
            return $value
        }
    } "The exact authenticated empty contributor history was not exposed."
    $periods = Find-ById $window "CareContributorPeriodsList"
    $periodItems = $periods.FindAll(
        [System.Windows.Automation.TreeScope]::Children,
        [System.Windows.Automation.Condition]::TrueCondition)
    if ($periodItems.Count -ne 0) {
        throw "The empty authoritative contributor history invented a period."
    }
    $refresh = Find-ById $window "RefreshCareContributorDashboardButton"
    if ($null -eq $refresh -or -not $refresh.Current.IsKeyboardFocusable -or
        [string]::IsNullOrWhiteSpace($refresh.Current.AccessKey)) {
        throw "The authenticated contributor refresh lacks a keyboard access key."
    }
    Invoke-ById $window "RefreshCareContributorDashboardButton" | Out-Null
    $status = Wait-For {
        $element = Find-ById $window "CareContributorDashboardStatus"
        if ($null -ne $element -and $element.Current.Name -eq
            "Loaded 0 CARE periods from the authenticated local ledger fixture. This dashboard is read-only. Provider requests 0. Payout attempts 0.") {
            return $element
        }
    } "The authenticated contributor refresh was not announced truthfully."
    $null = Wait-For {
        $element = Find-ById $window "CareContributorDashboardSummary"
        if ($element.Current.HasKeyboardFocus) { return $true }
    } "Authenticated contributor refresh did not focus the updated summary."

    $headingName = [string]$heading.Current.Name
    $boundaryName = [string]$boundary.Current.Name
    $refreshAccessKey = [string]$refresh.Current.AccessKey
    $statusName = [string]$status.Current.Name
    Stop-OwnedProcess $desktopProcess
    Stop-OwnedProcess $serverProcess
    $serverDiagnostics = $serverOutputTask.GetAwaiter().GetResult() +
        $serverErrorTask.GetAwaiter().GetResult()
    if ($serverDiagnostics.Contains($token, [StringComparison]::Ordinal) -or
        $serverDiagnostics.Contains($contributorPseudonym, [StringComparison]::Ordinal)) {
        throw "Contributor credentials or pseudonym entered server diagnostics."
    }

    [ordered]@{
        schema_version = 1
        ungated_fixture_denied = $true
        health_contributor_api = [bool]$health.careContributorDashboardApi
        health_contributor_rate_limit = [bool]$health.careContributorDashboardRateLimit
        health_operations_api = [bool]$health.careOperationsApi
        heading = $headingName
        boundary = $boundaryName
        contributor_pseudonym = $contributorPseudonym
        periods = 0
        allocated_cents = 0
        paid_cents = 0
        outstanding_cents = 0
        data_source = "authenticated local CARE ledger fixture"
        refresh_keyboard_access_key = $refreshAccessKey
        refresh_status = $statusName
        summary_focus_after_refresh = $true
        server_diagnostic_secret_matches = 0
        server_diagnostic_pseudonym_matches = 0
        provider_requests = 0
        payout_attempts = 0
        production_actions = 0
    } | ConvertTo-Json -Depth 3 | Set-Content -LiteralPath $evidencePath -Encoding utf8NoBOM

    "P11 authenticated contributor loopback native UIA=$headingName"
    "P11 contributor fixture default-off denial=PASS"
    "P11 contributor token-to-subject ledger composition=PASS"
    "P11 contributor empty-history no-fabrication and refresh focus=PASS"
    "P11 contributor diagnostic token/pseudonym matches=0"
    "P11 contributor provider requests=0 payout attempts=0 production actions=0"
    "P11 contributor loopback UIA evidence=$evidencePath"
}
finally {
    Stop-OwnedProcess $desktopProcess
    Stop-OwnedProcess $deniedProcess
    Stop-OwnedProcess $serverProcess
    if ($null -ne $httpClient) { $httpClient.Dispose() }
    if (Test-Path -LiteralPath $signingKeyPath -PathType Leaf) {
        [IO.File]::Delete($signingKeyPath)
    }
    $token = $null
}
