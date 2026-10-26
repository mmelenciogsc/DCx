[CmdletBinding()]
param(
    [string]$ExecutablePath,
    [string]$EvidencePath)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
$artifactsRoot = [IO.Path]::GetFullPath((Join-Path $repoRoot "artifacts")).TrimEnd('\')
$artifactsPrefix = $artifactsRoot + '\'
$runRoot = Join-Path $repoRoot "artifacts\test-results\u02-prerequisite-review-$([Guid]::NewGuid().ToString('N'))"
if ([string]::IsNullOrWhiteSpace($ExecutablePath)) {
    $ExecutablePath = Join-Path $repoRoot "src\DCx.Desktop\bin\Release\net10.0-windows10.0.19041.0\DCx.exe"
}
if ([string]::IsNullOrWhiteSpace($EvidencePath)) {
    New-Item -ItemType Directory -Path $runRoot | Out-Null
    $EvidencePath = Join-Path $runRoot "validation.json"
}

$executable = [IO.Path]::GetFullPath($ExecutablePath)
$evidence = [IO.Path]::GetFullPath($EvidencePath)
$repoPrefix = [IO.Path]::GetFullPath($repoRoot).TrimEnd('\') + '\'
if (-not (Test-Path -LiteralPath $executable -PathType Leaf) -or
    -not $executable.StartsWith($repoPrefix, [StringComparison]::OrdinalIgnoreCase) -or
    -not $evidence.StartsWith($artifactsPrefix, [StringComparison]::OrdinalIgnoreCase) -or
    (Test-Path -LiteralPath $evidence) -or
    -not (Test-Path -LiteralPath (Split-Path -Parent $evidence) -PathType Container)) {
    throw "The U02 prerequisite-review validation paths are invalid or unsafe."
}

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

function Wait-For([scriptblock]$condition, [string]$failureMessage, [int]$attempts = 50) {
    for ($attempt = 0; $attempt -lt $attempts; $attempt++) {
        $result = & $condition
        if ($result) { return $result }
        Start-Sleep -Milliseconds 200
    }
    throw $failureMessage
}

function Get-Window([int]$processId) {
    $condition = [Windows.Automation.PropertyCondition]::new(
        [Windows.Automation.AutomationElement]::ProcessIdProperty,
        $processId)
    return [Windows.Automation.AutomationElement]::RootElement.FindFirst(
        [Windows.Automation.TreeScope]::Children,
        $condition)
}

function Find-ById($root, [string]$automationId) {
    $condition = [Windows.Automation.PropertyCondition]::new(
        [Windows.Automation.AutomationElement]::AutomationIdProperty,
        $automationId)
    return $root.FindFirst([Windows.Automation.TreeScope]::Descendants, $condition)
}

function Get-Value($element) {
    return ([Windows.Automation.ValuePattern]$element.GetCurrentPattern(
        [Windows.Automation.ValuePattern]::Pattern)).Current.Value
}

$previousFixture = $env:DCX_PREREQUISITE_REVIEW_FIXTURE
$deniedProcess = $null
$process = $null
try {
    $env:DCX_PREREQUISITE_REVIEW_FIXTURE = $null
    $deniedProcess = Start-Process $executable -ArgumentList "--prerequisite-review-fixture" -PassThru
    if (-not $deniedProcess.WaitForExit(5000) -or $deniedProcess.ExitCode -ne 2) {
        throw "The U02 prerequisite-review fixture did not deny an ungated launch."
    }
    $deniedProcess = $null

    $env:DCX_PREREQUISITE_REVIEW_FIXTURE = "1"
    $process = Start-Process $executable -ArgumentList "--prerequisite-review-fixture" -PassThru
    $window = Wait-For { Get-Window $process.Id } "The U02 prerequisite-review window did not start."
    # WPF does not consistently expose a Window-level AutomationProperties.AutomationId
    # through the desktop UIA root. The title is the stable accessible identity; named
    # child controls below prove the review surface itself.
    if ($window.Current.Name -ne "Windows prerequisite review") {
        throw "The prerequisite-review window identity was not stable."
    }

    $heading = Wait-For { Find-ById $window "PrerequisiteReviewHeading" } "The prerequisite-review heading was not exposed."
    $position = Wait-For { Find-ById $window "PrerequisiteReviewPosition" } "The prerequisite position was not exposed."
    $boundary = Wait-For { Find-ById $window "PrerequisiteReviewBoundary" } "The review-only boundary was not exposed."
    $name = Wait-For { Find-ById $window "PrerequisiteName" } "The prerequisite name was not exposed."
    $publisher = Wait-For { Find-ById $window "PrerequisitePublisher" } "The prerequisite publisher was not exposed."
    $officialUri = Wait-For { Find-ById $window "PrerequisiteOfficialUri" } "The official URI was not exposed."
    $termsUri = Wait-For { Find-ById $window "PrerequisiteTermsUri" } "The terms URI was not exposed."
    $privilege = Wait-For { Find-ById $window "PrerequisitePrivilegeDisclosure" } "The privilege disclosure was not exposed."
    $details = Wait-For { Find-ById $window "PrerequisiteReadOnlyDetails" } "The read-only details were not exposed."
    $terms = Wait-For { Find-ById $window "PrerequisiteTermsAcknowledgement" } "The terms acknowledgement was not exposed."
    $install = Wait-For { Find-ById $window "PrerequisiteInstallApproval" } "The installation approval was not exposed."
    $decline = Wait-For { Find-ById $window "PrerequisiteDeclineButton" } "The decline button was not exposed."
    $retry = Wait-For { Find-ById $window "PrerequisiteRetryButton" } "The retry button was not exposed."
    $openOfficial = Wait-For { Find-ById $window "PrerequisiteOpenOfficialResourceButton" } "The official-resource handoff button was not exposed."
    $continue = Wait-For { Find-ById $window "PrerequisiteContinueButton" } "The continue button was not exposed."
    $status = Wait-For { Find-ById $window "PrerequisiteReviewStatus" } "The prerequisite-review live status was not exposed."

    if ($heading.Current.Name -ne "Review Windows prerequisite" -or
        $position.Current.Name -ne "Prerequisite 1 of 3" -or
        $boundary.Current.Name -ne "Consent-bound official-resource handoff boundary" -or
        $boundary.Current.HelpText -ne "" -or
        $boundary.Current.Name -eq "" -or
        $boundary.Current.Name -notlike "Consent-bound*") {
        throw "The first prerequisite review disclosure was not exact."
    }
    if ((Get-Value $name) -ne "Microsoft Edge WebView2 Runtime" -or
        (Get-Value $publisher) -ne "Microsoft Corporation" -or
        (Get-Value $officialUri) -ne "https://developer.microsoft.com/microsoft-edge/webview2/consumer/" -or
        (Get-Value $termsUri) -ne "https://www.microsoft.com/servicesagreement" -or
        (Get-Value $privilege) -notlike "The reviewed prerequisite does not require elevation*" -or
        (Get-Value $details) -notlike "Identifier: webview2-runtime*External official resource handoff authorized: no*Downloads authorized: no*Installer execution authorized: no*Package manager authorized: no*Elevation authorized: no*PowerShell invocation authorized: no") {
        throw "The first prerequisite did not expose its exact read-only review data."
    }

    $termsToggle = [Windows.Automation.TogglePattern]$terms.GetCurrentPattern([Windows.Automation.TogglePattern]::Pattern)
    $installToggle = [Windows.Automation.TogglePattern]$install.GetCurrentPattern([Windows.Automation.TogglePattern]::Pattern)
    if ($termsToggle.Current.ToggleState -ne [Windows.Automation.ToggleState]::Off -or
        $installToggle.Current.ToggleState -ne [Windows.Automation.ToggleState]::Off -or
        $continue.Current.IsEnabled -or
        $openOfficial.Current.IsEnabled) {
        throw "Separate prerequisite consents did not begin unchecked with bounded actions disabled."
    }
    $accessKeys = @($terms.Current.AccessKey, $install.Current.AccessKey, $decline.Current.AccessKey, $retry.Current.AccessKey, $openOfficial.Current.AccessKey, $continue.Current.AccessKey)
    if ($accessKeys -contains "" -or @($accessKeys | Select-Object -Unique).Count -ne $accessKeys.Count) {
        throw "Prerequisite consent and action access keys are absent or not distinct."
    }
    $childrenBefore = @(Get-CimInstance Win32_Process -Filter "ParentProcessId=$($process.Id)").Count
    $termsToggle.Toggle()
    $installToggle.Toggle()
    $null = Wait-For { $continue.Current.IsEnabled -and $openOfficial.Current.IsEnabled } "Bounded actions were not enabled after both separate consents."
    $openOfficial.GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke()
    $handoffStatus = "Fixture recorded the exact reviewed official-resource handoff without opening an external application. The one-use installation approval was reset. DCx did not download source files, install software, start an installer or package manager, request elevation, or invoke PowerShell."
    $null = Wait-For {
        $candidate = Find-ById $window "PrerequisiteReviewStatus"
        $candidate.Current.Name -eq $handoffStatus
    } "The fixture official-resource handoff status was not exposed."
    $openOfficial = Find-ById $window "PrerequisiteOpenOfficialResourceButton"
    $install = Find-ById $window "PrerequisiteInstallApproval"
    $installToggle = [Windows.Automation.TogglePattern]$install.GetCurrentPattern([Windows.Automation.TogglePattern]::Pattern)
    if ($installToggle.Current.ToggleState -ne [Windows.Automation.ToggleState]::Off -or
        $openOfficial.Current.IsEnabled) {
        throw "The fixture official-resource handoff did not consume the one-use approval."
    }
    $installToggle.Toggle()
    $null = Wait-For { $continue.Current.IsEnabled } "Continue was not re-enabled after a fresh one-use installation approval."
    $continue.GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke()
    $null = Wait-For { (Get-Value $name) -eq "Docker Desktop for Windows" } "Continue did not move to exactly one next prerequisite."
    $position = Find-ById $window "PrerequisiteReviewPosition"
    $terms = Find-ById $window "PrerequisiteTermsAcknowledgement"
    $install = Find-ById $window "PrerequisiteInstallApproval"
    $continue = Find-ById $window "PrerequisiteContinueButton"
    $termsToggle = [Windows.Automation.TogglePattern]$terms.GetCurrentPattern([Windows.Automation.TogglePattern]::Pattern)
    $installToggle = [Windows.Automation.TogglePattern]$install.GetCurrentPattern([Windows.Automation.TogglePattern]::Pattern)
    if ($position.Current.Name -ne "Prerequisite 2 of 3" -or
        $termsToggle.Current.ToggleState -ne [Windows.Automation.ToggleState]::Off -or
        $installToggle.Current.ToggleState -ne [Windows.Automation.ToggleState]::Off -or
        $continue.Current.IsEnabled -or
        (Get-Value (Find-ById $window "PrerequisitePrivilegeDisclosure")) -notlike "An external vendor installation may request administrator elevation*") {
        throw "A previous prerequisite decision leaked into the next item or elevation was not disclosed."
    }

    (Find-ById $window "PrerequisiteDeclineButton").GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke()
    $null = Wait-For { (Find-ById $window "PrerequisiteContinueButton").Current.IsEnabled } "Declining did not enable the bounded continue action."
    (Find-ById $window "PrerequisiteContinueButton").GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke()
    $null = Wait-For { (Get-Value (Find-ById $window "PrerequisiteName")) -eq "Windows Subsystem for Linux 2 (WSL 2)" } "The declined item did not continue to the next prerequisite."
    $retry = Find-ById $window "PrerequisiteRetryButton"
    $retry.GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke()
    $terms = Find-ById $window "PrerequisiteTermsAcknowledgement"
    $install = Find-ById $window "PrerequisiteInstallApproval"
    $termsToggle = [Windows.Automation.TogglePattern]$terms.GetCurrentPattern([Windows.Automation.TogglePattern]::Pattern)
    $installToggle = [Windows.Automation.TogglePattern]$install.GetCurrentPattern([Windows.Automation.TogglePattern]::Pattern)
    if ($termsToggle.Current.ToggleState -ne [Windows.Automation.ToggleState]::Off -or
        $installToggle.Current.ToggleState -ne [Windows.Automation.ToggleState]::Off) {
        throw "Retry did not reset the current prerequisite review consents."
    }
    $termsToggle.Toggle()
    $installToggle.Toggle()
    (Find-ById $window "PrerequisiteContinueButton").GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke()
    $null = Wait-For {
        $candidate = Find-ById $window "PrerequisiteReviewStatus"
        $candidate.Current.Name -eq "Prerequisite review is complete. Decisions were recorded. Official resources handed off: 1. Downloads 0. Installers started 0. Elevation requests 0. PowerShell invocations 0."
    } "The completed review did not report its inert authority boundary."
    $childrenAfter = @(Get-CimInstance Win32_Process -Filter "ParentProcessId=$($process.Id)").Count
    if ($childrenAfter -ne $childrenBefore) {
        throw "The prerequisite-review fixture started a child process."
    }

    $result = [ordered]@{
        captured_at_utc = (Get-Date).ToUniversalTime().ToString("O")
        fixture_gate_denied_without_environment = $true
        review_window = [string]$window.Current.Name
        first_prerequisite = "Microsoft Edge WebView2 Runtime"
        one_prerequisite_at_a_time = $true
        terms_initially_unchecked = $true
        install_approval_initially_unchecked = $true
        distinct_access_keys = $accessKeys
        decline_and_retry_validated = $true
        official_resource_handoff_fixture_recorded = $true
        official_resource_handoff_status = $handoffStatus
        external_child_processes_started = 0
        downloads_authorized = $false
        installers_started = 0
        elevation_requests = 0
        powershell_invocations = 0
    }
    $result | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $evidence -Encoding utf8
    Write-Host "U02 prerequisite-review UIA=PASS"
    Write-Host "U02 review only: downloads=0 installers=0 elevation=0 PowerShell=0"
    Write-Host "U02 evidence=$evidence"
    [pscustomobject]$result
}
finally {
    if ($null -ne $process -and -not $process.HasExited) {
        $null = $process.CloseMainWindow()
        if (-not $process.WaitForExit(5000)) { $process.Kill() }
    }
    if ($null -ne $deniedProcess -and -not $deniedProcess.HasExited) {
        $deniedProcess.Kill()
    }
    $env:DCX_PREREQUISITE_REVIEW_FIXTURE = $previousFixture
}
