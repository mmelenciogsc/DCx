[CmdletBinding()]
param(
    [string]$RunRoot
)

$ErrorActionPreference = "Stop"
if ($PSVersionTable.PSEdition -ne "Core" -or $PSVersionTable.PSVersion.Major -lt 7) {
    throw "Whisper registration validation requires PowerShell 7 or later so it can load the .NET production assembly."
}
$repoRoot = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($RunRoot)) {
    $RunRoot = Join-Path $repoRoot "artifacts\test-results\p06-whisper-$([Guid]::NewGuid().ToString('N'))"
}
$runRoot = [IO.Path]::GetFullPath($RunRoot)
$allowedRoot = [IO.Path]::GetFullPath((Join-Path $repoRoot "artifacts\test-results"))
if (-not $runRoot.StartsWith($allowedRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw "The Whisper UIA run root must be beneath the repository test-results directory."
}

$exe = Join-Path $repoRoot "src\DCx.Desktop\bin\Release\net10.0-windows10.0.19041.0\DCx.exe"
$fakeWhisper = Join-Path $repoRoot "tests\DCx.FakeWhisperCli\bin\Release\net10.0\DCx.FakeWhisperCli.exe"
$coreAssembly = Join-Path $repoRoot "src\DCx.Core\bin\Release\net10.0\DCx.Core.dll"
$dataRoot = Join-Path $runRoot "data"
$model = Join-Path $runRoot "fixture-model.bin"
$runtimeLicense = Join-Path $runRoot "runtime-license.txt"
$modelLicense = Join-Path $runRoot "model-license.md"
$audio = Join-Path $runRoot "fixture.wav"
$evidencePath = Join-Path $runRoot "whisper-registration-evidence.json"
$previousDataRoot = $env:DCX_DATA_ROOT
$previousOfflineStartup = $env:DCX_OFFLINE_STARTUP
$process = $null

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

function Wait-For([scriptblock]$Condition, [string]$FailureMessage, [int]$Attempts = 80) {
    for ($attempt = 0; $attempt -lt $Attempts; $attempt++) {
        $result = & $Condition
        if ($result) { return $result }
        Start-Sleep -Milliseconds 250
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

function Set-Value($Root, [string]$AutomationId, [string]$Value) {
    $element = Wait-For { Find-ById $Root $AutomationId } "The $AutomationId field was not exposed."
    $element.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue($Value)
}

function Invoke-ById($Root, [string]$AutomationId) {
    $element = Wait-For { Find-ById $Root $AutomationId } "The $AutomationId action was not exposed."
    $element.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    return $element
}

if (-not (Test-Path -LiteralPath $exe -PathType Leaf)) { throw "Build DCx Desktop before running Whisper UIA validation." }
if (-not (Test-Path -LiteralPath $fakeWhisper -PathType Leaf)) { throw "Build the fake Whisper CLI before running validation." }
New-Item -ItemType Directory -Path $runRoot -Force | Out-Null
[IO.File]::WriteAllText($model, "fixture whisper model")
[IO.File]::WriteAllText($runtimeLicense, "Fixture runtime license.`r`nComplete second line.")
[IO.File]::WriteAllText($modelLicense, "Fixture model license.`nComplete second line.")
[IO.File]::WriteAllBytes($audio, [byte[]]@(
    [byte][char]'R', [byte][char]'I', [byte][char]'F', [byte][char]'F', 4, 0, 0, 0,
    [byte][char]'W', [byte][char]'A', [byte][char]'V', [byte][char]'E'))

$executableDigest = (Get-FileHash -LiteralPath $fakeWhisper -Algorithm SHA256).Hash.ToLowerInvariant()
$modelDigest = (Get-FileHash -LiteralPath $model -Algorithm SHA256).Hash.ToLowerInvariant()
$runtimeLicenseDigest = (Get-FileHash -LiteralPath $runtimeLicense -Algorithm SHA256).Hash.ToLowerInvariant()
$modelLicenseDigest = (Get-FileHash -LiteralPath $modelLicense -Algorithm SHA256).Hash.ToLowerInvariant()

$env:DCX_DATA_ROOT = $dataRoot
$env:DCX_OFFLINE_STARTUP = "1"
$process = Start-Process $exe -PassThru
try {
    $mainWindow = Wait-For {
        $condition = [System.Windows.Automation.PropertyCondition]::new(
            [System.Windows.Automation.AutomationElement]::ProcessIdProperty,
            $process.Id)
        [System.Windows.Automation.AutomationElement]::RootElement.FindFirst(
            [System.Windows.Automation.TreeScope]::Children,
            $condition)
    } "The DCx main window did not appear."

    Invoke-ById $mainWindow "LocalModelsButton" | Out-Null
    $localModelsWindow = Wait-For {
        Find-WindowByProcessAndId $process.Id "LocalModelsWindow"
    } "The accessible local-model settings window did not open."
    $null = Wait-For {
        $status = Find-ById $localModelsWindow "LocalModelsStatus"
        if ($null -ne $status -and
            ($status.Current.Name -like "Found * installed model records.*" -or
             $status.Current.Name -like "Installed local models could not be read safely.*")) { $status }
    } "The bounded Ollama inventory refresh did not finish." 160

    $approval = Wait-For {
        Find-ById $localModelsWindow "ApproveWhisperBackendBox"
    } "The explicit Whisper approval checkbox was not exposed."
    $approvalToggle = $approval.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern)
    if ($approvalToggle.Current.ToggleState -ne [System.Windows.Automation.ToggleState]::Off) {
        throw "Whisper approval did not default to unchecked."
    }
    $current = Find-ById $localModelsWindow "CurrentWhisperBackend"
    if ($null -eq $current -or $current.Current.Name -ne "Transcription: none selected.") {
        throw "The initial empty Whisper registration was not exposed accessibly."
    }

    Set-Value $localModelsWindow "WhisperExecutablePath" $fakeWhisper
    Set-Value $localModelsWindow "WhisperModelPath" $model
    Set-Value $localModelsWindow "WhisperRuntimeLicensePath" $runtimeLicense
    Set-Value $localModelsWindow "WhisperModelLicensePath" $modelLicense
    Invoke-ById $localModelsWindow "ReviewWhisperBackendButton" | Out-Null

    $details = Wait-For {
        $candidate = Find-ById $localModelsWindow "WhisperBackendDetails"
        if ($null -ne $candidate -and
            $candidate.Current.Name -like "*SHA-256 $executableDigest*SHA-256 $modelDigest*" -and
            $candidate.Current.Name -like "*Runtime license SHA-256 $runtimeLicenseDigest*" -and
            $candidate.Current.Name -like "*Model license SHA-256 $modelLicenseDigest*") { $candidate }
    } "The exact four Whisper file digests were not exposed for review."
    $licenseText = Find-ById $localModelsWindow "WhisperLicenseText"
    $licenseValue = $licenseText.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).Current.Value
    if ($licenseValue -notlike "*WHISPER RUNTIME LICENSE*" -or
        $licenseValue -notlike "*Fixture runtime license*" -or
        $licenseValue -notlike "*WHISPER MODEL LICENSE*" -or
        $licenseValue -notlike "*Fixture model license*") {
        throw "Both complete Whisper licenses were not exposed through UI Automation."
    }
    if ($approvalToggle.Current.ToggleState -ne [System.Windows.Automation.ToggleState]::Off) {
        throw "Whisper approval changed without user action."
    }
    $whisperActionIds = @(
        "BrowseWhisperExecutableButton",
        "BrowseWhisperModelButton",
        "BrowseWhisperRuntimeLicenseButton",
        "BrowseWhisperModelLicenseButton",
        "ReviewWhisperBackendButton",
        "ApproveWhisperBackendBox",
        "SaveWhisperBackendButton",
        "ClearWhisperBackendButton")
    $accessKeys = @()
    foreach ($automationId in $whisperActionIds) {
        $action = Find-ById $localModelsWindow $automationId
        if ($null -eq $action -or -not $action.Current.IsKeyboardFocusable -or
            [string]::IsNullOrWhiteSpace($action.Current.AccessKey)) {
            throw "The $automationId action lacks a keyboard-focusable access key."
        }
        $accessKeys += $action.Current.AccessKey
    }
    if (@($accessKeys | Sort-Object -Unique).Count -ne $accessKeys.Count) {
        throw "Whisper registration actions expose duplicate access keys."
    }

    Invoke-ById $localModelsWindow "SaveWhisperBackendButton" | Out-Null
    $null = Wait-For {
        $status = Find-ById $localModelsWindow "LocalModelsStatus"
        $null -ne $status -and $status.Current.Name -like "Whisper registration was not saved.*"
    } "Saving without explicit Whisper approval did not fail closed."

    $approvalToggle.Toggle()
    Invoke-ById $localModelsWindow "SaveWhisperBackendButton" | Out-Null
    $current = Wait-For {
        $candidate = Find-ById $localModelsWindow "CurrentWhisperBackend"
        if ($null -ne $candidate -and
            $candidate.Current.Name -like "Transcription: DCx.FakeWhisperCli.exe with fixture-model.bin.*" -and
            $candidate.Current.Name -like "*Executable digest $executableDigest*" -and
            $candidate.Current.Name -like "*Model digest $modelDigest*") { $candidate }
    } "The approved hash-pinned Whisper registration was not exposed."
    if ($approvalToggle.Current.ToggleState -ne [System.Windows.Automation.ToggleState]::Off) {
        throw "Whisper approval was not reset after saving."
    }
    if ($null -ne (Find-ById $localModelsWindow "DownloadWhisperBackendButton")) {
        throw "Whisper registration unexpectedly exposed a download action."
    }
    $buttonCondition = [System.Windows.Automation.PropertyCondition]::new(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
        [System.Windows.Automation.ControlType]::Button)
    $buttons = $localModelsWindow.FindAll([System.Windows.Automation.TreeScope]::Descendants, $buttonCondition)
    if (@($buttons | Where-Object { $_.Current.Name -like "*download*Whisper*" }).Count -gt 0) {
        throw "A Whisper download action was exposed."
    }

    [void][Reflection.Assembly]::LoadFrom($coreAssembly)
    $store = [DCx.Core.LocalModels.WhisperBackendSelectionStore]::OpenAsync(
        $dataRoot, [Threading.CancellationToken]::None).GetAwaiter().GetResult()
    $state = $store.GetStateAsync([Threading.CancellationToken]::None).GetAwaiter().GetResult()
    if ($null -eq $state.Backend) { throw "The native UI did not persist the approved Whisper binding." }
    $transcriber = $state.Backend.CreateTranscriber([TimeSpan]::FromSeconds(5))
    $transcription = $transcriber.TranscribeWavAsync(
        $audio, "en", [Threading.CancellationToken]::None).GetAwaiter().GetResult()
    if ($transcription.Text -ne "DCx transcription fixture 42.") {
        throw "The UI-approved binding did not execute through the production Whisper adapter."
    }

    Invoke-ById $localModelsWindow "ClearWhisperBackendButton" | Out-Null
    $null = Wait-For {
        $candidate = Find-ById $localModelsWindow "CurrentWhisperBackend"
        $null -ne $candidate -and $candidate.Current.Name -eq "Transcription: none selected."
    } "The Whisper registration was not cleared."
    $cleared = $store.GetStateAsync([Threading.CancellationToken]::None).GetAwaiter().GetResult()
    if ($null -ne $cleared.Backend) { throw "The persisted Whisper binding remained after clear." }
    $sourceFiles = @($fakeWhisper, $model, $runtimeLicense, $modelLicense)
    if (@($sourceFiles | Where-Object { -not (Test-Path -LiteralPath $_ -PathType Leaf) }).Count -ne 0) {
        throw "Clearing registration deleted a source file."
    }

    $evidence = [ordered]@{
        captured_at_utc = (Get-Date).ToUniversalTime().ToString("O")
        native_uia = "pass"
        approval_default_unchecked = $true
        save_without_approval_denied = $true
        complete_runtime_license_exposed = $true
        complete_model_license_exposed = $true
        exact_file_digests_exposed = $true
        keyboard_focusable_unique_access_keys = $true
        executable_sha256 = $executableDigest
        model_sha256 = $modelDigest
        runtime_license_sha256 = $runtimeLicenseDigest
        model_license_sha256 = $modelLicenseDigest
        download_or_install_action_exposed = $false
        production_adapter_transcript = $transcription.Text
        cleared_registration = $true
        source_files_preserved = $true
        publisher_authenticity_validated = $false
        real_backend_accuracy_validated = $false
    }
    $evidence | ConvertTo-Json | Set-Content -LiteralPath $evidencePath -Encoding utf8
    "Whisper approval default unchecked=PASS"
    "Whisper save without approval denied=PASS"
    "Complete runtime/model licenses UIA=PASS"
    "Exact four-file digest review UIA=PASS"
    "UI-approved production adapter transcript=$($transcription.Text)"
    "Clear registration preserves source files=PASS"
    "Whisper download/install action absent=PASS"
    "Whisper registration evidence=$evidencePath"
}
finally {
    if ($null -ne $process -and -not $process.HasExited) {
        $null = $process.CloseMainWindow()
        if (-not $process.WaitForExit(5000)) { $process.Kill($true) }
    }
    $env:DCX_DATA_ROOT = $previousDataRoot
    $env:DCX_OFFLINE_STARTUP = $previousOfflineStartup
}
