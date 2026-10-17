[CmdletBinding()]
param(
    [string]$ExecutablePath,
    [string]$DataRoot,
    [string]$EvidencePath)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
$artifactsRoot = [IO.Path]::GetFullPath((Join-Path $repoRoot "artifacts")).TrimEnd('\')
$artifactsPrefix = $artifactsRoot + '\'
$standaloneRoot = Join-Path $repoRoot "artifacts\test-results\p13-webview2-$([Guid]::NewGuid().ToString('N'))"
if ([string]::IsNullOrWhiteSpace($ExecutablePath)) {
    $ExecutablePath = Join-Path $repoRoot "src\DCx.Desktop\bin\Release\net10.0-windows10.0.19041.0\DCx.exe"
}
if ([string]::IsNullOrWhiteSpace($DataRoot)) {
    New-Item -ItemType Directory -Path $standaloneRoot | Out-Null
    $DataRoot = Join-Path $standaloneRoot "data"
}
if ([string]::IsNullOrWhiteSpace($EvidencePath)) {
    if (-not (Test-Path -LiteralPath $standaloneRoot -PathType Container)) {
        New-Item -ItemType Directory -Path $standaloneRoot | Out-Null
    }
    $EvidencePath = Join-Path $standaloneRoot "validation.json"
}

$executable = [IO.Path]::GetFullPath($ExecutablePath)
$data = [IO.Path]::GetFullPath($DataRoot)
$evidence = [IO.Path]::GetFullPath($EvidencePath)
$repoPrefix = [IO.Path]::GetFullPath($repoRoot).TrimEnd('\') + '\'
if (-not (Test-Path -LiteralPath $executable -PathType Leaf) -or
    -not $executable.StartsWith($repoPrefix, [StringComparison]::OrdinalIgnoreCase) -or
    -not $data.StartsWith($artifactsPrefix, [StringComparison]::OrdinalIgnoreCase) -or
    -not $evidence.StartsWith($artifactsPrefix, [StringComparison]::OrdinalIgnoreCase) -or
    [IO.Path]::GetPathRoot($data).TrimEnd('\') -eq $data.TrimEnd('\') -or
    [IO.Path]::GetPathRoot($evidence).TrimEnd('\') -eq $evidence.TrimEnd('\') -or
    (Test-Path -LiteralPath $data) -or
    (Test-Path -LiteralPath $evidence) -or
    -not (Test-Path -LiteralPath (Split-Path -Parent $data) -PathType Container) -or
    -not (Test-Path -LiteralPath (Split-Path -Parent $evidence) -PathType Container)) {
    throw "The WebView2 validation paths are invalid, unsafe or already exist."
}
foreach ($path in @($executable, (Split-Path -Parent $data), (Split-Path -Parent $evidence))) {
    $current = Get-Item -LiteralPath $path -Force
    while ($null -ne $current) {
        if (($current.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "A WebView2 validation path has an unsafe reparse boundary."
        }
        if ($current.FullName.TrimEnd('\').Equals($repoRoot.TrimEnd('\'), [StringComparison]::OrdinalIgnoreCase) -or
            $current.FullName.TrimEnd('\').Equals($artifactsRoot, [StringComparison]::OrdinalIgnoreCase)) {
            break
        }
        $current = $current.Parent
    }
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

function Get-MainWindow([int]$processId) {
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

$previousDataRoot = $env:DCX_DATA_ROOT
$previousFixture = $env:DCX_WEBVIEW2_UNAVAILABLE_FIXTURE
$deniedProcess = $null
$process = $null
try {
    $env:DCX_DATA_ROOT = $data
    $env:DCX_WEBVIEW2_UNAVAILABLE_FIXTURE = $null
    $deniedProcess = Start-Process $executable -ArgumentList "--webview2-unavailable-fixture" -PassThru
    if (-not $deniedProcess.WaitForExit(5000) -or $deniedProcess.ExitCode -ne 2) {
        throw "The missing-runtime fixture did not deny an ungated launch."
    }
    $deniedProcess = $null

    $env:DCX_WEBVIEW2_UNAVAILABLE_FIXTURE = "1"
    $process = Start-Process $executable -ArgumentList "--webview2-unavailable-fixture" -PassThru
    $window = Wait-For { Get-MainWindow $process.Id } "The missing-runtime DCx window did not start."
    $panel = Wait-For { Find-ById $window "WebView2PrerequisitePanel" } "The WebView2 prerequisite panel was not exposed."
    $heading = Wait-For { Find-ById $window "WebView2PrerequisiteHeading" } "The WebView2 prerequisite heading was not exposed."
    $explanation = Wait-For { Find-ById $window "WebView2PrerequisiteExplanation" } "The WebView2 explanation was not exposed."
    $address = Wait-For { Find-ById $window "WebView2DownloadAddress" } "The official WebView2 address was not exposed."
    $copy = Wait-For { Find-ById $window "CopyWebView2DownloadAddressButton" } "The copy-address action was not exposed."
    $recheck = Wait-For { Find-ById $window "RecheckWebView2RuntimeButton" } "The runtime recheck action was not exposed."
    $status = Wait-For { Find-ById $window "StatusText" } "The missing-runtime live status was not exposed."
    $newTab = Wait-For { Find-ById $window "NewTabButton" } "The new-tab control was not exposed."
    $go = Wait-For { Find-ById $window "GoButton" } "The navigation control was not exposed."
    $profiles = Wait-For { Find-ById $window "ProfilesBox" } "The browser-profile control was not exposed."
    $agent = Wait-For { Find-ById $window "AgentDrawerButton" } "The browser-agent control was not exposed."
    $content = Wait-For { Find-ById $window "ContentViewerButton" } "The independent content viewer was not exposed."
    if ($panel.Current.Name -ne "WebView2 Runtime required" -or
        $heading.Current.Name -ne "WebView2 Runtime required" -or
        $explanation.Current.Name -notlike "DCx cannot open web pages*No download or installation was attempted*" -or
        $copy.Current.Name -ne "Copy official Microsoft WebView2 download address" -or
        $recheck.Current.Name -ne "Recheck WebView2 Runtime availability") {
        throw "The missing-runtime disclosure or actions were not exact."
    }
    $addressValue = $address.GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern).Current.Value
    if ($addressValue -ne "https://developer.microsoft.com/microsoft-edge/webview2/") {
        throw "The missing-runtime UI did not expose the reviewed official address."
    }
    if ($newTab.Current.IsEnabled -or $go.Current.IsEnabled -or $profiles.Current.IsEnabled -or
        $agent.Current.IsEnabled -or -not $content.Current.IsEnabled -or
        $null -ne (Find-ById $window "BrowserTabs")) {
        throw "Browser controls did not fail closed while independent local tools remained available."
    }
    $expectedStatus = "WebView2 Runtime is unavailable. Browser commands are unavailable. No download or installation was attempted."
    $null = Wait-For { $status.Current.Name -eq $expectedStatus } "The missing-runtime status was not exact."
    $heading.SetFocus()
    $null = Wait-For { $heading.Current.HasKeyboardFocus } "The missing-runtime heading did not accept keyboard focus."
    $recheck.GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke()
    $null = Wait-For {
        $status.Current.Name -eq $expectedStatus -and $recheck.Current.IsEnabled
    } "The local-only runtime recheck did not return to the accessible fail-closed state."
    if (Test-Path -LiteralPath (Join-Path $data "WebView2")) {
        throw "The unavailable-runtime flow created a WebView2 profile or process-data directory."
    }

    $result = [ordered]@{
        captured_at_utc = (Get-Date).ToUniversalTime().ToString("O")
        fixture_gate_denied_without_environment = $true
        prerequisite_panel_name = [string]$panel.Current.Name
        prerequisite_heading_keyboard_focus = $true
        official_address = $addressValue
        browser_commands_disabled = $true
        independent_content_viewer_enabled = $true
        recheck_fail_closed = $true
        webview2_profile_created = $false
        download_attempted = $false
        install_attempted = $false
        production_actions = 0
    }
    $result | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $evidence -Encoding utf8
    Write-Host "P13 WebView2 missing-runtime fixture gate=PASS"
    Write-Host "P13 WebView2 prerequisite UIA=$($panel.Current.Name)"
    Write-Host "P13 WebView2 browser commands fail closed=PASS"
    Write-Host "P13 WebView2 independent local content viewer remains enabled=PASS"
    Write-Host "P13 WebView2 local-only recheck=PASS"
    Write-Host "P13 WebView2 downloads=0 installs=0 production actions=0"
    Write-Host "P13 WebView2 evidence=$evidence"
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
    $env:DCX_DATA_ROOT = $previousDataRoot
    $env:DCX_WEBVIEW2_UNAVAILABLE_FIXTURE = $previousFixture
}
