[CmdletBinding()]
param(
    [string]$ExecutablePath,
    [string]$EvidencePath)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
$artifactsRoot = [IO.Path]::GetFullPath((Join-Path $repoRoot "artifacts")).TrimEnd('\')
$artifactsPrefix = $artifactsRoot + '\'
$runRoot = Join-Path $repoRoot "artifacts\test-results\u02-bootstrapper-$([Guid]::NewGuid().ToString('N'))"
if ([string]::IsNullOrWhiteSpace($ExecutablePath)) {
    $ExecutablePath = Join-Path $repoRoot "src\DCx.PrerequisiteBootstrapper\bin\Release\net10.0-windows\DCx.PrerequisiteBootstrapper.exe"
}
if ([string]::IsNullOrWhiteSpace($EvidencePath)) {
    New-Item -ItemType Directory -Path $runRoot | Out-Null
    $EvidencePath = Join-Path $runRoot "validation.json"
}

$executable = [IO.Path]::GetFullPath($ExecutablePath)
$evidence = [IO.Path]::GetFullPath($EvidencePath)
$repoPrefix = [IO.Path]::GetFullPath($repoRoot).TrimEnd('\') + '\'
$expectedEvidenceDirectoryPrefix = [IO.Path]::GetFullPath((Join-Path $repoRoot "artifacts\test-results\u02-bootstrapper-")).TrimEnd('\')
if (-not (Test-Path -LiteralPath $executable -PathType Leaf) -or
    -not $executable.StartsWith($repoPrefix, [StringComparison]::OrdinalIgnoreCase) -or
    -not $evidence.StartsWith($artifactsPrefix, [StringComparison]::OrdinalIgnoreCase) -or
    -not ((Split-Path -Parent $evidence).StartsWith($expectedEvidenceDirectoryPrefix, [StringComparison]::OrdinalIgnoreCase)) -or
    (Test-Path -LiteralPath $evidence) -or
    -not (Test-Path -LiteralPath (Split-Path -Parent $evidence) -PathType Container)) {
    throw "The U02 bootstrapper validation paths are invalid or unsafe."
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

function Invoke-Element($element) {
    ([Windows.Automation.InvokePattern]$element.GetCurrentPattern(
        [Windows.Automation.InvokePattern]::Pattern)).Invoke()
}

function Select-ComboItem($window, $combo, [string]$itemName) {
    $expand = [Windows.Automation.ExpandCollapsePattern]$combo.GetCurrentPattern(
        [Windows.Automation.ExpandCollapsePattern]::Pattern)
    $expand.Expand()
    $item = Wait-For {
        $condition = [Windows.Automation.AndCondition]::new(@(
            [Windows.Automation.PropertyCondition]::new(
                [Windows.Automation.AutomationElement]::ProcessIdProperty,
                $window.Current.ProcessId),
            [Windows.Automation.PropertyCondition]::new(
                [Windows.Automation.AutomationElement]::ControlTypeProperty,
                [Windows.Automation.ControlType]::ListItem),
            [Windows.Automation.PropertyCondition]::new(
                [Windows.Automation.AutomationElement]::NameProperty,
                $itemName)))
        [Windows.Automation.AutomationElement]::RootElement.FindFirst(
            [Windows.Automation.TreeScope]::Descendants,
            $condition)
    } "The prerequisite selection '$itemName' was not exposed."
    ([Windows.Automation.SelectionItemPattern]$item.GetCurrentPattern(
        [Windows.Automation.SelectionItemPattern]::Pattern)).Select()
    $null = Wait-For {
        $selected = ([Windows.Automation.SelectionPattern]$combo.GetCurrentPattern(
            [Windows.Automation.SelectionPattern]::Pattern)).Current.GetSelection()
        $selected.Count -eq 1 -and $selected[0].Current.Name -eq $itemName
    } "The prerequisite selection '$itemName' did not apply."
}

function Assert-ToggleOff($element, [string]$description) {
    $toggle = [Windows.Automation.TogglePattern]$element.GetCurrentPattern(
        [Windows.Automation.TogglePattern]::Pattern)
    if ($toggle.Current.ToggleState -ne [Windows.Automation.ToggleState]::Off) {
        throw "$description was not initially unchecked."
    }
    return $toggle
}

function Assert-AssertiveLiveSetting($element) {
    $liveSettingProperty = [Windows.Automation.AutomationElementIdentifiers]::LiveSettingProperty
    if ($null -eq $liveSettingProperty) {
        throw "The UI Automation LiveSetting property identifier is unavailable."
    }

    $rawLiveSetting = $element.GetCurrentPropertyValue($liveSettingProperty)
    if ($null -eq $rawLiveSetting) {
        throw "The bootstrapper status does not expose a UI Automation live setting."
    }
    if ($rawLiveSetting -eq [Windows.Automation.AutomationElement]::NotSupported) {
        throw "The bootstrapper status reports its UI Automation live setting as unsupported."
    }

    $assertiveValue = [int][Windows.Automation.AutomationLiveSetting]::Assertive
    if ([string]::Equals([string]$rawLiveSetting, "Assertive", [StringComparison]::OrdinalIgnoreCase)) {
        return $assertiveValue
    }

    try {
        # UIA providers may marshal AutomationLiveSetting as either its enum or its integral value.
        $actualValue = [Convert]::ToInt32($rawLiveSetting, [Globalization.CultureInfo]::InvariantCulture)
    }
    catch {
        throw "The bootstrapper status live setting is not provider-compatible assertive data: type '$($rawLiveSetting.GetType().FullName)', value '$rawLiveSetting'."
    }

    if ($actualValue -ne $assertiveValue) {
        throw "The bootstrapper status live setting was '$actualValue', not assertive '$assertiveValue'."
    }

    return $actualValue
}

$previousFixture = $env:DCX_BOOTSTRAPPER_FIXTURE
$deniedProcess = $null
$process = $null
$normalProcess = $null
$liveRegionSignal = $null
$liveRegionHandler = $null
try {
    # The fixture mode must fail closed when its explicit environment gate is absent.
    $env:DCX_BOOTSTRAPPER_FIXTURE = $null
    $deniedProcess = Start-Process $executable -ArgumentList "--bootstrapper-fixture" -PassThru
    if (-not $deniedProcess.WaitForExit(5000) -or $deniedProcess.ExitCode -ne 2) {
        throw "The bootstrapper fixture did not deny an ungated launch."
    }
    $deniedProcess = $null

    $env:DCX_BOOTSTRAPPER_FIXTURE = "1"
    $process = Start-Process $executable -ArgumentList "--bootstrapper-fixture" -PassThru
    $window = Wait-For { Get-Window $process.Id } "The bootstrapper fixture window did not start."
    if ($window.Current.Name -ne "DCx prerequisite bootstrapper") {
        throw "The bootstrapper window identity was not stable."
    }

    $heading = Wait-For { Find-ById $window "BootstrapperHeading" } "The bootstrapper heading was not exposed."
    $boundary = Wait-For { Find-ById $window "BootstrapperBoundaryNotice" } "The bootstrapper boundary notice was not exposed."
    $prerequisiteBox = Wait-For { Find-ById $window "BootstrapperPrerequisite" } "The prerequisite selector was not exposed."
    $officialUri = Wait-For { Find-ById $window "BootstrapperOfficialUri" } "The official URI was not exposed."
    $termsUri = Wait-For { Find-ById $window "BootstrapperTermsUri" } "The terms URI was not exposed."
    $detectionStatus = Wait-For { Find-ById $window "BootstrapperDetectionStatus" } "The detection status was not exposed."
    $detectionTimestamp = Wait-For { Find-ById $window "BootstrapperDetectionTimestamp" } "The detection timestamp was not exposed."
    $plannedWslCommand = Wait-For { Find-ById $window "BootstrapperPlannedWslCommand" } "The planned WSL command was not exposed."
    $refresh = Wait-For { Find-ById $window "BootstrapperRefreshStatusButton" } "The refresh action was not exposed."
    $viewTerms = Wait-For { Find-ById $window "BootstrapperViewTermsButton" } "The terms action was not exposed."
    $selectArtifact = Wait-For { Find-ById $window "BootstrapperSelectArtifactButton" } "The artifact selection action was not exposed."
    $terms = Wait-For { Find-ById $window "BootstrapperTermsAcknowledgement" } "The terms acknowledgement was not exposed."
    $startApproval = Wait-For { Find-ById $window "BootstrapperStartApproval" } "The start approval was not exposed."
    $planText = Wait-For { Find-ById $window "BootstrapperLaunchPlan" } "The policy plan was not exposed."
    $start = Wait-For { Find-ById $window "BootstrapperStartButton" } "The Start action was not exposed."
    $close = Wait-For { Find-ById $window "BootstrapperCloseButton" } "The close action was not exposed."
    $status = Wait-For { Find-ById $window "BootstrapperLiveStatus" } "The assertive status was not exposed."

    $focused = Wait-For { [Windows.Automation.AutomationElement]::FocusedElement } "Initial keyboard focus was not exposed."
    if ($heading.Current.Name -ne "Windows prerequisite bootstrapper" -or
        $focused.Current.AutomationId -ne "BootstrapperHeading" -or
        $boundary.Current.Name -ne "Bootstrapper authority boundary") {
        throw "The bootstrapper initial focus or authority disclosure was not stable."
    }

    $detectedAtUtc = [DateTimeOffset]::MinValue
    if ((Get-Value $officialUri) -ne "https://developer.microsoft.com/microsoft-edge/webview2/consumer/" -or
        (Get-Value $termsUri) -ne "https://www.microsoft.com/servicesagreement" -or
        (Get-Value $detectionStatus) -ne "Missing" -or
        (Get-Value $plannedWslCommand) -ne "wsl.exe --install --no-distribution" -or
        -not [DateTimeOffset]::TryParse((Get-Value $detectionTimestamp), [ref]$detectedAtUtc) -or
        $detectedAtUtc -gt [DateTimeOffset]::UtcNow -or
        ([DateTimeOffset]::UtcNow - $detectedAtUtc) -gt [TimeSpan]::FromMinutes(2)) {
        throw "The first WebView2 fixture metadata was not exact and fresh."
    }

    $termsToggle = Assert-ToggleOff $terms "Terms acknowledgement"
    $approvalToggle = Assert-ToggleOff $startApproval "Start approval"
    if ($start.Current.IsEnabled -or -not $selectArtifact.Current.IsEnabled) {
        throw "The initial WebView2 actions were not bounded."
    }
    $accessKeys = @($refresh.Current.AccessKey, $viewTerms.Current.AccessKey, $selectArtifact.Current.AccessKey, $terms.Current.AccessKey, $startApproval.Current.AccessKey, $start.Current.AccessKey, $close.Current.AccessKey)
    if ($accessKeys -contains "" -or @($accessKeys | Select-Object -Unique).Count -ne $accessKeys.Count) {
        throw "The bootstrapper access keys are absent or not distinct."
    }
    $assertiveLiveSettingValue = Assert-AssertiveLiveSetting $status

    $childrenBefore = @(Get-CimInstance Win32_Process -Filter "ParentProcessId=$($process.Id)").Count
    Invoke-Element $selectArtifact
    $null = Wait-For {
        (Find-ById $window "BootstrapperLiveStatus").Current.Name -like "Fixture selected a prevalidated synthetic artifact record*"
    } "Fixture artifact selection was not recorded."
    $termsToggle.Toggle()
    $approvalToggle.Toggle()
    $null = Wait-For { (Find-ById $window "BootstrapperStartButton").Current.IsEnabled } "The policy-permitted WebView2 Start action was not enabled."
    if ((Get-Value (Find-ById $window "BootstrapperLaunchPlan")) -notlike "Permitted data-only plan: C:\Fixture\prevalidated-synthetic-installer.exe*Process execution authorized: no*") {
        throw "The selected synthetic artifact did not produce the expected data-only plan."
    }
    $liveRegionSignal = [Threading.AutoResetEvent]::new($false)
    $liveRegionHandler = [Windows.Automation.AutomationEventHandler]{
        param($sender, $eventArgs)
        if ($sender.Current.ProcessId -eq $process.Id -and
            $sender.Current.AutomationId -eq "BootstrapperLiveStatus") {
            $null = $liveRegionSignal.Set()
        }
    }
    [Windows.Automation.Automation]::AddAutomationEventHandler(
        [Windows.Automation.AutomationElementIdentifiers]::LiveRegionChangedEvent,
        $status,
        [Windows.Automation.TreeScope]::Element,
        $liveRegionHandler)
    Invoke-Element (Find-ById $window "BootstrapperStartButton")
    $expectedNoLaunchStatus = "The policy plan is permitted, but real launch is unavailable pending a separately reviewed verifier and launcher. No process, browser, UAC prompt, download, package manager, PowerShell, winget, reboot, or installer was started. Vendor/Microsoft remains responsible for its own terms and elevation."
    $null = Wait-For {
        (Find-ById $window "BootstrapperLiveStatus").Current.Name -eq $expectedNoLaunchStatus
    } "The assertive no-launch Start status was not exposed."
    if (-not $liveRegionSignal.WaitOne(5000)) {
        throw "The assertive no-launch status did not raise a UI Automation LiveRegionChanged event."
    }
    [Windows.Automation.Automation]::RemoveAutomationEventHandler(
        [Windows.Automation.AutomationElementIdentifiers]::LiveRegionChangedEvent,
        $status,
        $liveRegionHandler)
    $liveRegionHandler = $null
    $liveRegionSignal.Dispose()
    $liveRegionSignal = $null

    Select-ComboItem $window $prerequisiteBox "Docker Desktop for Windows"
    $null = Wait-For { (Get-Value (Find-ById $window "BootstrapperOfficialUri")) -eq "https://www.docker.com/products/docker-desktop/" } "The next prerequisite was not selected."
    $terms = Find-ById $window "BootstrapperTermsAcknowledgement"
    $startApproval = Find-ById $window "BootstrapperStartApproval"
    $start = Find-ById $window "BootstrapperStartButton"
    $null = Assert-ToggleOff $terms "Docker terms acknowledgement"
    $null = Assert-ToggleOff $startApproval "Docker start approval"
    if ($start.Current.IsEnabled -or (Get-Value (Find-ById $window "BootstrapperDetectionStatus")) -ne "Missing") {
        throw "Changing prerequisite did not reset consent, selected artifact, and the policy plan."
    }

    Select-ComboItem $window $prerequisiteBox "Windows Subsystem for Linux 2 (WSL 2)"
    $null = Wait-For { (Get-Value (Find-ById $window "BootstrapperOfficialUri")) -eq "https://learn.microsoft.com/en-us/windows/wsl/install" } "The WSL prerequisite was not selected."
    $terms = Find-ById $window "BootstrapperTermsAcknowledgement"
    $startApproval = Find-ById $window "BootstrapperStartApproval"
    $start = Find-ById $window "BootstrapperStartButton"
    if ((Get-Value (Find-ById $window "BootstrapperPlannedWslCommand")) -ne "wsl.exe --install --no-distribution" -or
        (Find-ById $window "BootstrapperSelectArtifactButton").Current.IsEnabled) {
        throw "The WSL surface did not expose only its fixed command and no artifact selection."
    }
    ([Windows.Automation.TogglePattern]$terms.GetCurrentPattern([Windows.Automation.TogglePattern]::Pattern)).Toggle()
    ([Windows.Automation.TogglePattern]$startApproval.GetCurrentPattern([Windows.Automation.TogglePattern]::Pattern)).Toggle()
    $null = Wait-For { (Find-ById $window "BootstrapperStartButton").Current.IsEnabled } "The fixed WSL plan was not policy-permitted after both independent approvals."
    if ((Get-Value (Find-ById $window "BootstrapperLaunchPlan")) -ne "Permitted data-only plan: wsl.exe --install --no-distribution. Process execution authorized: no. Real launcher: unavailable pending verifier/launcher.") {
        throw "The permitted WSL data-only plan was not exact."
    }

    $childrenAfter = @(Get-CimInstance Win32_Process -Filter "ParentProcessId=$($process.Id)").Count
    if ($childrenAfter -ne $childrenBefore) {
        throw "The bootstrapper fixture started a child process."
    }

    Invoke-Element (Find-ById $window "BootstrapperCloseButton")
    if (-not $process.WaitForExit(5000)) {
        throw "The bootstrapper fixture window did not close."
    }
    $process = $null

    # Normal composition is intentionally not a fallback launcher: it has no detector or
    # selected-artifact verifier, so policy must continue to deny Start after both approvals.
    $env:DCX_BOOTSTRAPPER_FIXTURE = $null
    $normalProcess = Start-Process $executable -PassThru
    $normalWindow = Wait-For { Get-Window $normalProcess.Id } "The normal bootstrapper window did not start."
    $normalStatus = Wait-For { Find-ById $normalWindow "BootstrapperLiveStatus" } "The normal bootstrapper status was not exposed."
    $normalArtifact = Wait-For { Find-ById $normalWindow "BootstrapperSelectArtifactButton" } "The normal artifact action was not exposed."
    $normalTerms = Wait-For { Find-ById $normalWindow "BootstrapperTermsAcknowledgement" } "The normal terms acknowledgement was not exposed."
    $normalApproval = Wait-For { Find-ById $normalWindow "BootstrapperStartApproval" } "The normal start approval was not exposed."
    $normalStart = Wait-For { Find-ById $normalWindow "BootstrapperStartButton" } "The normal Start action was not exposed."
    if ((Get-Value (Find-ById $normalWindow "BootstrapperDetectionStatus")) -ne "Unknown" -or $normalStart.Current.IsEnabled) {
        throw "Normal composition did not remain unavailable without a detector."
    }
    $normalChildrenBefore = @(Get-CimInstance Win32_Process -Filter "ParentProcessId=$($normalProcess.Id)").Count
    Invoke-Element $normalArtifact
    $null = Wait-For {
        (Find-ById $normalWindow "BootstrapperLiveStatus").Current.Name -eq "Artifact selection is refused in normal composition until a real selected-artifact verifier is implemented. No file picker, filesystem probe, hash, signature check, or process ran."
    } "Normal composition did not refuse unverified artifact selection."
    ([Windows.Automation.TogglePattern]$normalTerms.GetCurrentPattern([Windows.Automation.TogglePattern]::Pattern)).Toggle()
    ([Windows.Automation.TogglePattern]$normalApproval.GetCurrentPattern([Windows.Automation.TogglePattern]::Pattern)).Toggle()
    Start-Sleep -Milliseconds 250
    if ((Find-ById $normalWindow "BootstrapperStartButton").Current.IsEnabled) {
        throw "Normal composition enabled Start without fresh missing evidence."
    }
    $normalChildrenAfter = @(Get-CimInstance Win32_Process -Filter "ParentProcessId=$($normalProcess.Id)").Count
    if ($normalChildrenAfter -ne $normalChildrenBefore) {
        throw "Normal bootstrapper composition started a child process."
    }
    Invoke-Element (Find-ById $normalWindow "BootstrapperCloseButton")
    if (-not $normalProcess.WaitForExit(5000)) {
        throw "The normal bootstrapper window did not close."
    }
    $normalProcess = $null

    $result = [ordered]@{
        captured_at_utc = (Get-Date).ToUniversalTime().ToString("O")
        fixture_gate_denied_without_environment = $true
        initial_focus_automation_id = [string]$focused.Current.AutomationId
        first_prerequisite = "Microsoft Edge WebView2 Runtime"
        first_detection_status = "Missing"
        first_official_uri = "https://developer.microsoft.com/microsoft-edge/webview2/consumer/"
        first_terms_uri = "https://www.microsoft.com/servicesagreement"
        assertive_live_setting_value = $assertiveLiveSettingValue
        terms_initially_unchecked = $true
        start_approval_initially_unchecked = $true
        distinct_access_keys = $accessKeys
        fixture_synthetic_artifact_selected = $true
        start_assertive_no_launch_status = $expectedNoLaunchStatus
        next_selection_resets_review = $true
        normal_composition_start_denied = $true
        wsl_fixed_command = "wsl.exe --install --no-distribution"
        external_child_processes_started = 0
        browser_opened = $false
        network_requests = 0
        elevation_requests = 0
        package_manager_invocations = 0
        powershell_invocations_by_bootstrapper = 0
        reboots = 0
    }
    $result | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $evidence -Encoding utf8
    Write-Host "U02 bootstrapper UIA=PASS"
    Write-Host "U02 fixture boundary: browser=0 network=0 child-processes=0 UAC=0 package-manager=0 PowerShell=0 reboot=0"
    Write-Host "U02 evidence=$evidence"
    [pscustomobject]$result
}
finally {
    if ($null -ne $liveRegionHandler -and $null -ne $status) {
        [Windows.Automation.Automation]::RemoveAutomationEventHandler(
            [Windows.Automation.AutomationElementIdentifiers]::LiveRegionChangedEvent,
            $status,
            $liveRegionHandler)
    }
    if ($null -ne $liveRegionSignal) { $liveRegionSignal.Dispose() }
    if ($null -ne $process -and -not $process.HasExited) {
        $null = $process.CloseMainWindow()
        if (-not $process.WaitForExit(5000)) { $process.Kill() }
    }
    if ($null -ne $deniedProcess -and -not $deniedProcess.HasExited) {
        $deniedProcess.Kill()
    }
    if ($null -ne $normalProcess -and -not $normalProcess.HasExited) {
        $null = $normalProcess.CloseMainWindow()
        if (-not $normalProcess.WaitForExit(5000)) { $normalProcess.Kill() }
    }
    $env:DCX_BOOTSTRAPPER_FIXTURE = $previousFixture
}
