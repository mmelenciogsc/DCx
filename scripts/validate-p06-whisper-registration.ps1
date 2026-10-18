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
$ffmpegLicense = Join-Path $runRoot "ffmpeg-license.txt"
$invalidDependencyArchive = Join-Path $runRoot "invalid-runtime.zip"
$stagingFixtureRoot = Join-Path $runRoot "staging-fixture-source"
$stagingArchive = Join-Path $runRoot "synthetic-whisper-runtime.zip"
$stagingDataRoot = Join-Path $runRoot "staging-data"
$audio = Join-Path $runRoot "fixture.wav"
$timeoutAudio = Join-Path $runRoot "timeout.wav"
$mp3Audio = Join-Path $runRoot "fixture.mp3"
$benchmarkManifest = Join-Path $runRoot "transcription-benchmark.json"
$slowBenchmarkManifest = Join-Path $runRoot "transcription-benchmark-slow.json"
$embeddingBenchmarkManifest = Join-Path $runRoot "embedding-benchmark.json"
$benchmarkEvidencePath = Join-Path $dataRoot "local-models\benchmark-evidence\transcription-latest.json"
$evidencePath = Join-Path $runRoot "whisper-registration-evidence.json"
$previousDataRoot = $env:DCX_DATA_ROOT
$previousOfflineStartup = $env:DCX_OFFLINE_STARTUP
$previousStagingFixture = $env:DCX_WHISPER_STAGING_FIXTURE
$previousStagingFixtureRoot = $env:DCX_WHISPER_STAGING_FIXTURE_ROOT
$process = $null
$stagingProcess = $null
$cleanupFailure = $null

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
[IO.File]::WriteAllText($ffmpegLicense, "Fixture FFmpeg license.`nComplete second line.")
[IO.File]::WriteAllBytes($invalidDependencyArchive, [byte[]]@(0x50, 0x4b, 0x03, 0x04, 1, 2, 3, 4))
New-Item -ItemType Directory -Path (Join-Path $stagingFixtureRoot "Release") -Force | Out-Null
[IO.File]::WriteAllBytes(
    (Join-Path $stagingFixtureRoot "Release\whisper-cli.exe"),
    [byte[]]@(1, 2, 3, 4))
[IO.File]::WriteAllBytes(
    (Join-Path $stagingFixtureRoot "Release\cublas64_11.dll"),
    [byte[]]@(5, 6, 7, 8))
[IO.File]::WriteAllText(
    (Join-Path $stagingFixtureRoot "LICENSE"),
    "Synthetic MIT fixture license")
[IO.Compression.ZipFile]::CreateFromDirectory(
    $stagingFixtureRoot,
    $stagingArchive,
    [IO.Compression.CompressionLevel]::Optimal,
    $false)
[IO.File]::WriteAllBytes($audio, [byte[]]@(
    [byte][char]'R', [byte][char]'I', [byte][char]'F', [byte][char]'F', 4, 0, 0, 0,
    [byte][char]'W', [byte][char]'A', [byte][char]'V', [byte][char]'E'))
[IO.File]::Copy($audio, $timeoutAudio)
[IO.File]::WriteAllBytes($mp3Audio, [byte[]]@(
    [byte][char]'I', [byte][char]'D', [byte][char]'3', 4, 0, 0, 0, 0, 0, 0))
$benchmarkJson = @'
{
  "schemaVersion": 1,
  "kind": "transcription",
  "name": "DCx native UIA transcription fixture",
  "provenance": { "source": "DCx synthetic validation fixture", "license": "CC0-1.0" },
  "limits": {
    "maximumWordErrorRate": 0.0,
    "maximumCharacterErrorRate": 0.0,
    "resources": {
      "maximumElapsedMilliseconds": 60000,
      "maximumPeakWorkingSetBytes": 1073741824,
      "maximumPeakGpuBytes": null,
      "requireUnloadConfirmation": true
    }
  },
  "fixtures": [
    { "id": "uia-speech-1", "waveFile": "fixture.wav", "expectedText": "DCx transcription fixture 42." }
  ]
}
'@
[IO.File]::WriteAllText($benchmarkManifest, $benchmarkJson)
[IO.File]::WriteAllText(
    $slowBenchmarkManifest,
    $benchmarkJson.Replace('"fixture.wav"', '"timeout.wav"'))
$embeddingBenchmarkJson = @'
{
  "schemaVersion": 1,
  "kind": "embeddingRetrieval",
  "name": "DCx native UIA embedding fixture",
  "provenance": { "source": "DCx synthetic validation fixture", "license": "CC0-1.0" },
  "limits": {
    "k": 1,
    "minimumRecallAtK": 1.0,
    "minimumMeanReciprocalRank": 1.0,
    "resources": {
      "maximumElapsedMilliseconds": 5000,
      "maximumPeakWorkingSetBytes": 536870912,
      "maximumPeakGpuBytes": 536870912,
      "requireUnloadConfirmation": true
    }
  },
  "documents": [ { "id": "uia-document", "text": "Synthetic retrieval document." } ],
  "queries": [
    { "id": "uia-query", "text": "Synthetic retrieval query.", "relevantDocumentIds": ["uia-document"] }
  ]
}
'@
[IO.File]::WriteAllText($embeddingBenchmarkManifest, $embeddingBenchmarkJson)

$executableDigest = (Get-FileHash -LiteralPath $fakeWhisper -Algorithm SHA256).Hash.ToLowerInvariant()
$modelDigest = (Get-FileHash -LiteralPath $model -Algorithm SHA256).Hash.ToLowerInvariant()
$runtimeLicenseDigest = (Get-FileHash -LiteralPath $runtimeLicense -Algorithm SHA256).Hash.ToLowerInvariant()
$modelLicenseDigest = (Get-FileHash -LiteralPath $modelLicense -Algorithm SHA256).Hash.ToLowerInvariant()
$ffmpegLicenseDigest = (Get-FileHash -LiteralPath $ffmpegLicense -Algorithm SHA256).Hash.ToLowerInvariant()
$invalidDependencyDigest = (Get-FileHash -LiteralPath $invalidDependencyArchive -Algorithm SHA256).Hash.ToLowerInvariant()
$stagingArchiveDigest = (Get-FileHash -LiteralPath $stagingArchive -Algorithm SHA256).Hash.ToLowerInvariant()
$stagingDestination = Join-Path $stagingDataRoot "local-models\whisper-runtime\synthetic-whisper-runtime-$stagingArchiveDigest"
$stagingReceipt = Join-Path $stagingDestination ".dcx-runtime-receipt.json"
$benchmarkManifestDigest = (Get-FileHash -LiteralPath $benchmarkManifest -Algorithm SHA256).Hash.ToLowerInvariant()

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

    $hardwareProfile = Wait-For {
        $candidate = Find-ById $localModelsWindow "LocalHardwareProfile"
        if ($null -ne $candidate -and
            $candidate.Current.Name -like "Hardware profile. Captured UTC *" -and
            $candidate.Current.Name -like "*Processor 12th Gen Intel(R) Core(TM) i7-12650H. 16 logical processors.*" -and
            $candidate.Current.Name -like "*NVIDIA GeForce RTX 4060 Laptop GPU. Provider NVIDIA. Windows driver 32.0.16.1078. Reported graphics memory 8,585,740,288 bytes from HardwareInformation.qwMemorySize. Currently available graphics memory * bytes from NVIDIA NVML free memory.*" -and
            $candidate.Current.Name -like "*No external process or network request ran. Available capacity is volatile and must be rechecked before resource-intensive work.*") {
            $candidate
        }
    } "The native bounded hardware profile was not exposed accessibly."
    if (-not $hardwareProfile.Current.IsKeyboardFocusable) {
        throw "The native hardware profile is not keyboard focusable."
    }

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
    $currentFfmpeg = Find-ById $localModelsWindow "CurrentFfmpegBackend"
    if ($null -eq $currentFfmpeg -or $currentFfmpeg.Current.Name -ne "Media processor: none selected.") {
        throw "The initial empty FFmpeg registration was not exposed accessibly."
    }

    $dependencyCatalog = Wait-For {
        $candidate = Find-ById $localModelsWindow "DependencyCatalogSummary"
        if ($null -ne $candidate -and
            $candidate.Current.Name -like "Catalog dcx-p06-dependencies-2026-10-18.*" -and
            $candidate.Current.Name -like "*SHA-256 1ea3bfb04567757e0207cfd824fb5284faad2552617cbbe3330a13f66079e325*" -and
            $candidate.Current.Name -like "*4 immutable candidates*" -and
            $candidate.Current.Name -like "*Route observation is available only through the explicit HEAD-only action*" -and
            $candidate.Current.Name -like "*Payload contact is available only after that in-memory review and the separate default-off exact-plan transfer approval*" -and
            $candidate.Current.Name -like "*Transferred content remains inert*installation, extraction, pull, execution, registration, and selection are not authorized*") {
            $candidate
        }
    } "The immutable dependency catalog identity and no-authority notice were not exposed."
    $dependencyTransferPreflight = Wait-For {
        $candidate = Find-ById $localModelsWindow "DependencyTransferPreflight"
        if ($null -ne $candidate -and
            $candidate.Current.Name -like "Transfer preflight only. Plan SHA-256 a0d997c6298fdf5f592241928c6fd9564c6de0c3e023c9eb69bd1df96406c286.*" -and
            $candidate.Current.Name -like "*4 candidates. 9 immutable transfer items.*" -and
            $candidate.Current.Name -like "*1,281,058,770 network bytes. 3,098,988,452 minimum free bytes.*" -and
            $candidate.Current.Name -like "*Redirect policy SHA-256 *Approved redirect routes: none. The reviewed policy denies every redirect.*" -and
            $candidate.Current.Name -like "*reloads the authenticated catalog, requires exact-item approval, disables automatic redirects, proxy, credentials, preauthentication, cookies and decompression, and rolls back any partial stream*" -and
            $candidate.Current.Name -like "*bounded manual redirect policy is bound to the displayed digest and routes*" -and
            $candidate.Current.Name -like "*current policy has no routes; no real provider redirect is authorized*" -and
            $candidate.Current.Name -like "*No current in-memory provider-route review exists*HEAD-only route review*" -and
            $candidate.Current.Name -like "*private set coordinator serializes transfer, verification and cleanup*rechecks capacity*uses random incoming storage*protects a source-path-free receipt*atomically publishes only a complete plan*can rehash the completed set*" -and
            $candidate.Current.Name -like "*separately approved exact-plan transfer, cancellation, protected verification and abandoned-partial recovery*" -and
            $candidate.Current.Name -like "*Current provider redirects remain denied*" -and
            $candidate.Current.Name -like "*No transfer approval exists. Nothing was downloaded, installed, extracted, pulled, registered, selected, or executed.*") {
            $candidate
        }
    } "The exact dependency transfer preflight and no-authority notice were not exposed."
    if (-not $dependencyTransferPreflight.Current.IsKeyboardFocusable) {
        throw "The exact dependency transfer preflight is not keyboard focusable."
    }
    $dependencyTransferApproval = Find-ById $localModelsWindow "ApproveDependencyTransferBox"
    if ($dependencyTransferApproval.Current.Name -notlike "*redirect-policy digest and routes*") {
        throw "The dependency transfer approval did not name the reviewed redirect-policy authority."
    }
    $dependencyTransferToggle = $dependencyTransferApproval.GetCurrentPattern(
        [System.Windows.Automation.TogglePattern]::Pattern)
    $dependencyTransferStart = Find-ById $localModelsWindow "StartDependencyTransferButton"
    $dependencyTransferCancel = Find-ById $localModelsWindow "CancelDependencyTransferButton"
    $dependencyRouteReview = Find-ById $localModelsWindow "ReviewDependencyRoutesButton"
    $dependencyRouteReviewCancel = Find-ById $localModelsWindow "CancelDependencyRouteReviewButton"
    if ($dependencyTransferToggle.Current.ToggleState -ne [System.Windows.Automation.ToggleState]::Off -or
        $dependencyTransferApproval.Current.IsEnabled -or
        $dependencyTransferStart.Current.IsEnabled -or $dependencyTransferCancel.Current.IsEnabled -or
        $null -eq $dependencyRouteReview -or -not $dependencyRouteReview.Current.IsEnabled -or
        -not $dependencyRouteReview.Current.IsKeyboardFocusable -or
        $dependencyRouteReview.Current.Name -notlike "*HEAD requests only*" -or
        $dependencyRouteReview.Current.HelpText -notlike "*reads no response body or payload*" -or
        $dependencyRouteReview.Current.HelpText -notlike "*does not approve a transfer*" -or
        $null -eq $dependencyRouteReviewCancel -or $dependencyRouteReviewCancel.Current.IsEnabled) {
        throw "Production route review and dependency transfer did not begin at the explicit, default-off authority boundary."
    }
    $routeActionIds = @("ReviewDependencyRoutesButton", "CancelDependencyRouteReviewButton")
    $routeAccessKeys = foreach ($automationId in $routeActionIds) {
        $action = Find-ById $localModelsWindow $automationId
        if ([string]::IsNullOrWhiteSpace($action.Current.AccessKey)) {
            throw "The $automationId provider-route review control lacks an access key."
        }
        $action.Current.AccessKey
    }
    if (@($routeAccessKeys | Sort-Object -Unique).Count -ne $routeAccessKeys.Count) {
        throw "Provider-route review controls expose duplicate access keys."
    }
    $dependencyList = Find-ById $localModelsWindow "DependencyCandidatesList"
    if ($null -eq $dependencyList -or -not $dependencyList.Current.IsKeyboardFocusable) {
        throw "The audited dependency candidate list is not keyboard focusable."
    }
    $dependencyItems = $dependencyList.FindAll(
        [System.Windows.Automation.TreeScope]::Descendants,
        [System.Windows.Automation.PropertyCondition]::new(
            [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
            [System.Windows.Automation.ControlType]::ListItem))
    $runtimeCandidate = @($dependencyItems | Where-Object {
        $_.Current.Name -like "whisper.cpp b5454 Windows CUDA 11.8 x64 runtime.*"
    }) | Select-Object -First 1
    if ($null -eq $runtimeCandidate) {
        $observedDependencyItems = @($dependencyItems | ForEach-Object { $_.Current.Name }) -join " | "
        throw "The pinned Whisper runtime candidate was not exposed. Observed: $observedDependencyItems"
    }
    $runtimeCandidate.GetCurrentPattern(
        [System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
    $dependencyDetails = Wait-For {
        $candidate = Find-ById $localModelsWindow "DependencyCandidateDetails"
        if ($null -eq $candidate) { return $null }
        $value = $candidate.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).Current.Value
        if ($value -like "*Candidate whisper-cuda-runtime-b5454*" -and
            $value -like "*github.com/ggml-org/whisper.cpp/releases/download/b5454/whisper-bin-win-cuda-11.8.0-x64.zip*" -and
            $value -like "*Expected bytes: 284,761,231*" -and
            $value -like "*SHA-256: e81c86c542be6e3d7b7245392d4e1c52ab6e5c61dbe38721754bc0ab59d4d027*" -and
            $value -like "*Archive-contained licenses and notices require separate review after staging*" -and
            $value -like "*grants no network, download, extraction, install, pull, execution, legal, approval, or selection authority*") {
            $candidate
        }
    } "The pinned dependency source, size, digest, license limit, and no-authority notice were not exposed."
    Set-Value $localModelsWindow "DependencyArtifactPath" $invalidDependencyArchive
    $dependencyActionIds = @("BrowseDependencyArtifactButton", "ReviewDependencyArtifactButton")
    $dependencyAccessKeys = @()
    foreach ($automationId in $dependencyActionIds) {
        $action = Find-ById $localModelsWindow $automationId
        if ($null -eq $action -or -not $action.Current.IsKeyboardFocusable -or
            [string]::IsNullOrWhiteSpace($action.Current.AccessKey)) {
            throw "The $automationId action lacks a keyboard-focusable access key."
        }
        $dependencyAccessKeys += $action.Current.AccessKey
    }
    if (@($dependencyAccessKeys | Sort-Object -Unique).Count -ne $dependencyAccessKeys.Count) {
        throw "Dependency-review actions expose duplicate access keys."
    }
    Invoke-ById $localModelsWindow "ReviewDependencyArtifactButton" | Out-Null
    $null = Wait-For {
        $candidate = Find-ById $localModelsWindow "DependencyArtifactResult"
        if ($null -ne $candidate -and $candidate.Current.Name -like "Verification failed closed.*") { $candidate }
    } "A wrong-size staged dependency artifact did not fail closed."
    if ((Get-FileHash -LiteralPath $invalidDependencyArchive -Algorithm SHA256).Hash.ToLowerInvariant() -ne
        $invalidDependencyDigest) {
        throw "Rejected staged dependency content was changed."
    }
    $allButtons = $localModelsWindow.FindAll(
        [System.Windows.Automation.TreeScope]::Descendants,
        [System.Windows.Automation.PropertyCondition]::new(
            [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
            [System.Windows.Automation.ControlType]::Button))
    $forbiddenDependencyActions = @($allButtons | Where-Object {
        $_.Current.Name -match "(?i)\b(download|install|pull|extract|execute)\b"
    })
    if ($forbiddenDependencyActions.Count -ne 0) {
        throw "The local-model window exposed a dependency download/install/pull/extract/execute action."
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
    if ($null -eq $state.Backend -or $store.RequiresReapproval) {
        throw "The native UI did not persist a protected approved Whisper binding."
    }
    $transcriber = $state.Backend.CreateTranscriber([TimeSpan]::FromSeconds(5))
    $transcription = $transcriber.TranscribeWavAsync(
        $audio, "en", [Threading.CancellationToken]::None).GetAwaiter().GetResult()
    if ($transcription.Text -ne "DCx transcription fixture 42.") {
        throw "The UI-approved binding did not execute through the production Whisper adapter."
    }

    Invoke-ById $localModelsWindow "OpenBenchmarkReviewButton" | Out-Null
    $benchmarkWindow = Wait-For {
        Find-WindowByProcessAndId $process.Id "LocalModelBenchmarkWindow"
    } "The accessible local-model benchmark review window did not open."
    $benchmarkApproval = Wait-For {
        Find-ById $benchmarkWindow "ApproveBenchmarkRunBox"
    } "The explicit benchmark-run approval checkbox was not exposed."
    $benchmarkApprovalToggle = $benchmarkApproval.GetCurrentPattern(
        [System.Windows.Automation.TogglePattern]::Pattern)
    if ($benchmarkApprovalToggle.Current.ToggleState -ne [System.Windows.Automation.ToggleState]::Off) {
        throw "Benchmark approval did not default to unchecked."
    }
    Set-Value $benchmarkWindow "BenchmarkManifestPath" $benchmarkManifest
    Invoke-ById $benchmarkWindow "ReviewBenchmarkManifestButton" | Out-Null
    $benchmarkDetails = Wait-For {
        $candidate = Find-ById $benchmarkWindow "BenchmarkReviewDetails"
        if ($null -ne $candidate -and
            $candidate.Current.Name -like "*Type: transcription. Fixtures: 1.*" -and
            $candidate.Current.Name -like "*Manifest SHA-256: $benchmarkManifestDigest*" -and
            $candidate.Current.Name -match "Fixture-suite SHA-256: [0-9a-f]{64}" -and
            $candidate.Current.Name -match "Policy SHA-256: [0-9a-f]{64}" -and
            $candidate.Current.Name -match "Approved transcription backend binding SHA-256: [0-9a-f]{64}" -and
            $candidate.Current.Name -like "*maximum WER 0.00%*maximum CER 0.00%*") { $candidate }
    } "The benchmark review did not expose exact digests and thresholds." 160
    if ($benchmarkApprovalToggle.Current.ToggleState -ne [System.Windows.Automation.ToggleState]::Off) {
        throw "Benchmark approval changed without user action."
    }
    $benchmarkRun = Find-ById $benchmarkWindow "RunApprovedBenchmarkButton"
    if ($null -eq $benchmarkRun -or $benchmarkRun.Current.IsEnabled) {
        throw "The benchmark run action was enabled without explicit approval."
    }
    $benchmarkActionIds = @(
        "BrowseBenchmarkManifestButton", "ReviewBenchmarkManifestButton", "ApproveBenchmarkRunBox",
        "CloseBenchmarkWindowButton")
    $benchmarkAccessKeys = @()
    foreach ($automationId in $benchmarkActionIds) {
        $action = Find-ById $benchmarkWindow $automationId
        if ($null -eq $action -or -not $action.Current.IsKeyboardFocusable -or
            [string]::IsNullOrWhiteSpace($action.Current.AccessKey)) {
            throw "The $automationId benchmark action lacks a keyboard-focusable access key."
        }
        $benchmarkAccessKeys += $action.Current.AccessKey
    }
    if (@($benchmarkAccessKeys | Sort-Object -Unique).Count -ne $benchmarkAccessKeys.Count) {
        throw "Benchmark review actions expose duplicate access keys."
    }

    $benchmarkApprovalToggle.Toggle()
    $benchmarkRun = Wait-For {
        $candidate = Find-ById $benchmarkWindow "RunApprovedBenchmarkButton"
        if ($null -ne $candidate -and $candidate.Current.IsEnabled) { $candidate }
    } "Explicit approval did not enable the exact benchmark run."
    if (-not $benchmarkRun.Current.IsKeyboardFocusable -or
        [string]::IsNullOrWhiteSpace($benchmarkRun.Current.AccessKey) -or
        $benchmarkAccessKeys -contains $benchmarkRun.Current.AccessKey) {
        throw "The enabled benchmark run action lacks a unique keyboard access key."
    }
    $benchmarkAccessKeys += $benchmarkRun.Current.AccessKey
    $benchmarkRun.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    $benchmarkResult = Wait-For {
        $candidate = Find-ById $benchmarkWindow "BenchmarkAggregateResult"
        if ($null -ne $candidate -and $candidate.Current.Name -like "PASS.*Fixtures: 1.*WER: 0.00%*") { $candidate }
    } "The approved production-adapter benchmark did not return an aggregate passing result." 160
    if (-not $benchmarkResult.Current.HasKeyboardFocus) {
        throw "The completed aggregate benchmark result did not receive keyboard focus."
    }
    if ($benchmarkApprovalToggle.Current.ToggleState -ne [System.Windows.Automation.ToggleState]::Off) {
        throw "Benchmark approval was not revoked after the completed run."
    }
    if (-not (Test-Path -LiteralPath $benchmarkEvidencePath -PathType Leaf)) {
        throw "Aggregate benchmark evidence was not retained."
    }
    $benchmarkEvidenceJson = [IO.File]::ReadAllText($benchmarkEvidencePath)
    $benchmarkEvidence = $benchmarkEvidenceJson | ConvertFrom-Json
    if ($benchmarkEvidence.schemaVersion -ne 1 -or
        $benchmarkEvidence.result.passed -ne $true -or
        $benchmarkEvidence.result.fixtureCount -ne 1 -or
        $benchmarkEvidenceJson -like "*DCx transcription fixture 42*" -or
        $benchmarkEvidenceJson -like "*$runRoot*") {
        throw "Persisted benchmark evidence was not aggregate-only passing evidence."
    }
    $completedEvidenceDigest = (Get-FileHash -LiteralPath $benchmarkEvidencePath -Algorithm SHA256).Hash

    Set-Value $benchmarkWindow "BenchmarkManifestPath" $slowBenchmarkManifest
    $invalidated = Find-ById $benchmarkWindow "RunApprovedBenchmarkButton"
    if ($invalidated.Current.IsEnabled -or
        $benchmarkApprovalToggle.Current.ToggleState -ne [System.Windows.Automation.ToggleState]::Off) {
        throw "Changing the benchmark manifest path did not revoke exact review and approval."
    }
    Invoke-ById $benchmarkWindow "ReviewBenchmarkManifestButton" | Out-Null
    $null = Wait-For {
        $candidate = Find-ById $benchmarkWindow "ApproveBenchmarkRunBox"
        if ($null -ne $candidate -and $candidate.Current.IsEnabled) { $candidate }
    } "The slow cancellation fixture was not reviewed."
    $benchmarkApprovalToggle.Toggle()
    $benchmarkRun = Wait-For {
        $candidate = Find-ById $benchmarkWindow "RunApprovedBenchmarkButton"
        if ($null -ne $candidate -and $candidate.Current.IsEnabled) { $candidate }
    } "The reviewed slow benchmark was not enabled after approval."
    $benchmarkRun.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    $benchmarkCancel = Wait-For {
        $candidate = Find-ById $benchmarkWindow "CancelBenchmarkButton"
        if ($null -ne $candidate -and $candidate.Current.IsEnabled) { $candidate }
    } "Benchmark cancellation did not become available during processing."
    if (-not $benchmarkCancel.Current.IsKeyboardFocusable -or
        [string]::IsNullOrWhiteSpace($benchmarkCancel.Current.AccessKey) -or
        $benchmarkAccessKeys -contains $benchmarkCancel.Current.AccessKey) {
        throw "The enabled benchmark cancel action lacks a unique keyboard access key."
    }
    $benchmarkCancel.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    $null = Wait-For {
        $status = Find-ById $benchmarkWindow "LocalModelBenchmarkStatus"
        if ($null -ne $status -and $status.Current.Name -like "The local benchmark was canceled.*") { $status }
    } "The active local benchmark did not cancel." 160
    if ((Get-FileHash -LiteralPath $benchmarkEvidencePath -Algorithm SHA256).Hash -ne $completedEvidenceDigest) {
        throw "A canceled benchmark replaced the prior completed aggregate evidence."
    }
    Invoke-ById $benchmarkWindow "CloseBenchmarkWindowButton" | Out-Null

    Invoke-ById $mainWindow "ContentViewerButton" | Out-Null
    $contentViewer = Wait-For {
        Find-WindowByProcessAndId $process.Id "ContentViewerWindow"
    } "The accessible files and media viewer did not open."
    Set-Value $contentViewer "FilePathBox" $timeoutAudio
    Invoke-ById $contentViewer "OpenFileButton" | Out-Null
    $transcribeButton = Wait-For {
        $candidate = Find-ById $contentViewer "TranscribeWavButton"
        if ($null -ne $candidate -and $candidate.Current.IsEnabled) { $candidate }
    } "The admitted WAV did not expose the local transcription action."
    $cancelButton = Find-ById $contentViewer "CancelTranscriptionButton"
    if ($null -eq $cancelButton -or $cancelButton.Current.IsEnabled) {
        throw "Cancel transcription was enabled before an operation began."
    }
    $transcribeAccessKey = $transcribeButton.Current.AccessKey
    if (-not $transcribeButton.Current.IsKeyboardFocusable -or
        [string]::IsNullOrWhiteSpace($transcribeAccessKey)) {
        throw "The media transcription action lacks a keyboard-focusable access key."
    }
    $transcribeButton.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    $cancelButton = Wait-For {
        $candidate = Find-ById $contentViewer "CancelTranscriptionButton"
        if ($null -ne $candidate -and $candidate.Current.IsEnabled) { $candidate }
    } "Cancel transcription did not become available during local processing."
    if (-not $cancelButton.Current.IsKeyboardFocusable -or
        [string]::IsNullOrWhiteSpace($cancelButton.Current.AccessKey) -or
        $cancelButton.Current.AccessKey -eq $transcribeAccessKey) {
        throw "The active cancel-transcription action lacks a distinct keyboard access key."
    }
    $cancelButton.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    $null = Wait-For {
        $status = Find-ById $contentViewer "ContentStatusText"
        $null -ne $status -and $status.Current.Name -like "Media transcription canceled.*"
    } "User cancellation did not stop local media transcription."
    $canceledTranscript = Find-ById $contentViewer "MediaTranscriptText"
    if (-not [string]::IsNullOrEmpty($canceledTranscript.GetCurrentPattern(
            [System.Windows.Automation.ValuePattern]::Pattern).Current.Value)) {
        throw "A canceled local transcription retained partial transcript text."
    }

    Set-Value $contentViewer "FilePathBox" $audio
    Invoke-ById $contentViewer "OpenFileButton" | Out-Null
    $transcribeButton = Wait-For {
        $candidate = Find-ById $contentViewer "TranscribeWavButton"
        if ($null -ne $candidate -and $candidate.Current.IsEnabled) { $candidate }
    } "The replacement WAV did not expose transcription after cancellation."
    $transcribeButton.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    $null = Wait-For {
        $status = Find-ById $contentViewer "ContentStatusText"
        $null -ne $status -and $status.Current.Name -like "Media transcription complete in *Media remains paused."
    } "The accessible local WAV transcription did not complete."
    $transcriptElement = Find-ById $contentViewer "MediaTranscriptText"
    $viewerTranscript = $transcriptElement.GetCurrentPattern(
        [System.Windows.Automation.ValuePattern]::Pattern).Current.Value
    if ($viewerTranscript -ne "DCx transcription fixture 42." -or -not $transcriptElement.Current.HasKeyboardFocus) {
        throw "The completed local transcript was not exposed and focused through UI Automation."
    }

    $whisperStatePath = Join-Path $dataRoot "local-models\transcription.json"
    [IO.File]::AppendAllText($whisperStatePath, " ")
    Invoke-ById $localModelsWindow "RefreshLocalModelsButton" | Out-Null
    $null = Wait-For {
        $candidate = Find-ById $localModelsWindow "CurrentWhisperBackend"
        $null -ne $candidate -and
            $candidate.Current.Name -like "Transcription: a stored approval did not match its protected per-user anchor*"
    } "A replayed or changed Whisper approval did not expose the accessible reapproval warning."
    $rejectedWhisper = $store.GetStateAsync([Threading.CancellationToken]::None).GetAwaiter().GetResult()
    if ($null -ne $rejectedWhisper.Backend -or -not $store.RequiresReapproval) {
        throw "A changed Whisper approval remained usable without explicit reapproval."
    }

    Invoke-ById $localModelsWindow "ClearWhisperBackendButton" | Out-Null
    $null = Wait-For {
        $candidate = Find-ById $localModelsWindow "CurrentWhisperBackend"
        $null -ne $candidate -and $candidate.Current.Name -eq "Transcription: none selected."
    } "The Whisper registration was not cleared."
    $cleared = $store.GetStateAsync([Threading.CancellationToken]::None).GetAwaiter().GetResult()
    if ($null -ne $cleared.Backend) { throw "The persisted Whisper binding remained after clear." }
    Invoke-ById $localModelsWindow "OpenBenchmarkReviewButton" | Out-Null
    $unavailableBenchmarkWindow = Wait-For {
        Find-WindowByProcessAndId $process.Id "LocalModelBenchmarkWindow"
    } "The benchmark review window did not reopen for unavailable-backend validation."
    $null = Wait-For {
        $candidate = Find-ById $unavailableBenchmarkWindow "BenchmarkBackendAvailability"
        if ($null -ne $candidate -and
            $candidate.Current.Name -like "Transcription benchmark unavailable: no explicitly approved local Whisper backend*") { $candidate }
    } "The missing approved transcription backend was not exposed as unavailable."
    Set-Value $unavailableBenchmarkWindow "BenchmarkManifestPath" $benchmarkManifest
    Invoke-ById $unavailableBenchmarkWindow "ReviewBenchmarkManifestButton" | Out-Null
    $null = Wait-For {
        $status = Find-ById $unavailableBenchmarkWindow "LocalModelBenchmarkStatus"
        if ($null -ne $status -and
            $status.Current.Name -like "The exact transcription suite was reviewed, but running is unavailable*") { $status }
    } "A reviewed suite did not remain unavailable without an approved backend."
    $unavailableApproval = Find-ById $unavailableBenchmarkWindow "ApproveBenchmarkRunBox"
    $unavailableRun = Find-ById $unavailableBenchmarkWindow "RunApprovedBenchmarkButton"
    if ($unavailableApproval.Current.IsEnabled -or $unavailableRun.Current.IsEnabled) {
        throw "Benchmark approval or execution was enabled without an approved backend."
    }
    Set-Value $unavailableBenchmarkWindow "BenchmarkManifestPath" $embeddingBenchmarkManifest
    Invoke-ById $unavailableBenchmarkWindow "ReviewBenchmarkManifestButton" | Out-Null
    $null = Wait-For {
        $status = Find-ById $unavailableBenchmarkWindow "LocalModelBenchmarkStatus"
        if ($null -ne $status -and
            $status.Current.Name -like "The exact embedding suite was reviewed, but running is unavailable*") { $status }
    } "A reviewed embedding suite did not remain unavailable without an approved model."
    $embeddingDetails = Find-ById $unavailableBenchmarkWindow "BenchmarkReviewDetails"
    if ($null -eq $embeddingDetails -or
        $embeddingDetails.Current.Name -notlike "*Type: embedding retrieval. Documents: 1. Queries: 1.*" -or
        $embeddingDetails.Current.Name -notlike "*minimum recall 100.00%*minimum mean reciprocal rank 100.00%*" -or
        $embeddingDetails.Current.Name -notlike "*No approved digest-bound Ollama embedding model is selected.*" -or
        $unavailableApproval.Current.IsEnabled -or $unavailableRun.Current.IsEnabled) {
        throw "Embedding review did not expose exact unavailable thresholds and deny execution."
    }
    Invoke-ById $unavailableBenchmarkWindow "CloseBenchmarkWindowButton" | Out-Null
    $transcribeButton = Wait-For {
        $candidate = Find-ById $contentViewer "TranscribeWavButton"
        if ($null -ne $candidate -and $candidate.Current.IsEnabled) { $candidate }
    } "The admitted WAV action became inaccessible after registration was cleared."
    $transcribeButton.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    $null = Wait-For {
        $status = Find-ById $contentViewer "ContentStatusText"
        $null -ne $status -and $status.Current.Name -like "No local Whisper backend is registered.*"
    } "The viewer did not report the cleared Whisper registration."
    $clearedTranscript = Find-ById $contentViewer "MediaTranscriptText"
    if (-not [string]::IsNullOrEmpty($clearedTranscript.GetCurrentPattern(
            [System.Windows.Automation.ValuePattern]::Pattern).Current.Value)) {
        throw "The unavailable backend path retained stale transcript text."
    }

    Set-Value $localModelsWindow "FfmpegExecutablePath" $fakeWhisper
    Set-Value $localModelsWindow "FfmpegLicensePath" $ffmpegLicense
    Invoke-ById $localModelsWindow "ReviewFfmpegBackendButton" | Out-Null
    $ffmpegDetails = Wait-For {
        $candidate = Find-ById $localModelsWindow "FfmpegBackendDetails"
        if ($null -ne $candidate -and
            $candidate.Current.Name -like "*SHA-256 $executableDigest*" -and
            $candidate.Current.Name -like "*License SHA-256 $ffmpegLicenseDigest*" -and
            $candidate.Current.Name -like "*Windows signature status NotSigned*" -and
            $candidate.Current.Name -like "*does not create publisher authenticity*") { $candidate }
    } "The exact FFmpeg digests and real unsigned status were not exposed for review."
    $ffmpegLicenseElement = Find-ById $localModelsWindow "FfmpegLicenseText"
    $ffmpegLicenseValue = $ffmpegLicenseElement.GetCurrentPattern(
        [System.Windows.Automation.ValuePattern]::Pattern).Current.Value
    if ($ffmpegLicenseValue -notlike "*FFMPEG LICENSE*" -or
        $ffmpegLicenseValue -notlike "*Fixture FFmpeg license*" -or
        $ffmpegLicenseValue -notlike "*Complete second line.*") {
        throw "The complete FFmpeg license was not exposed through UI Automation."
    }
    $ffmpegApproval = Find-ById $localModelsWindow "ApproveFfmpegBackendBox"
    $ffmpegApprovalToggle = $ffmpegApproval.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern)
    if ($ffmpegApprovalToggle.Current.ToggleState -ne [System.Windows.Automation.ToggleState]::Off) {
        throw "FFmpeg approval did not default to unchecked."
    }
    $ffmpegActionIds = @(
        "BrowseFfmpegExecutableButton", "BrowseFfmpegLicenseButton", "ReviewFfmpegBackendButton",
        "ApproveFfmpegBackendBox", "SaveFfmpegBackendButton", "ClearFfmpegBackendButton")
    $ffmpegAccessKeys = @()
    foreach ($automationId in $ffmpegActionIds) {
        $action = Find-ById $localModelsWindow $automationId
        if ($null -eq $action -or -not $action.Current.IsKeyboardFocusable -or
            [string]::IsNullOrWhiteSpace($action.Current.AccessKey)) {
            throw "The $automationId action lacks a keyboard-focusable access key."
        }
        $ffmpegAccessKeys += $action.Current.AccessKey
    }
    if (@($ffmpegAccessKeys | Sort-Object -Unique).Count -ne $ffmpegAccessKeys.Count) {
        throw "FFmpeg registration actions expose duplicate access keys."
    }
    Invoke-ById $localModelsWindow "SaveFfmpegBackendButton" | Out-Null
    $null = Wait-For {
        $status = Find-ById $localModelsWindow "LocalModelsStatus"
        $null -ne $status -and $status.Current.Name -like "FFmpeg registration was not saved.*"
    } "Saving FFmpeg without explicit approval did not fail closed."
    $ffmpegApprovalToggle.Toggle()
    Invoke-ById $localModelsWindow "SaveFfmpegBackendButton" | Out-Null
    $currentFfmpeg = Wait-For {
        $candidate = Find-ById $localModelsWindow "CurrentFfmpegBackend"
        if ($null -ne $candidate -and $candidate.Current.Name -like "Media processor: DCx.FakeWhisperCli.exe.*" -and
            $candidate.Current.Name -like "*Executable digest $executableDigest*" -and
            $candidate.Current.Name -like "*Windows signature status NotSigned*") { $candidate }
    } "The approved hash-pinned FFmpeg registration was not exposed."
    if ($ffmpegApprovalToggle.Current.ToggleState -ne [System.Windows.Automation.ToggleState]::Off) {
        throw "FFmpeg approval was not reset after saving."
    }

    $ffmpegStore = [DCx.Core.LocalModels.FfmpegBackendSelectionStore]::OpenAsync(
        $dataRoot, [Threading.CancellationToken]::None).GetAwaiter().GetResult()
    $ffmpegState = $ffmpegStore.GetStateAsync([Threading.CancellationToken]::None).GetAwaiter().GetResult()
    if ($null -eq $ffmpegState.Backend -or $ffmpegStore.RequiresReapproval) {
        throw "The native UI did not persist a protected approved FFmpeg binding."
    }
    $null = $store.SetAsync($state.Backend, $true, [Threading.CancellationToken]::None).GetAwaiter().GetResult()

    Set-Value $contentViewer "FilePathBox" $mp3Audio
    Invoke-ById $contentViewer "OpenFileButton" | Out-Null
    $transcribeButton = Wait-For {
        $candidate = Find-ById $contentViewer "TranscribeWavButton"
        if ($null -ne $candidate -and $candidate.Current.IsEnabled) { $candidate }
    } "The admitted MP3 did not expose the local transcription action."
    $transcribeButton.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    $null = Wait-For {
        $status = Find-ById $contentViewer "ContentStatusText"
        $null -ne $status -and $status.Current.Name -like "Media transcription complete in *Media remains paused."
    } "The approved FFmpeg-to-Whisper MP3 pipeline did not complete."
    $mp3Transcript = (Find-ById $contentViewer "MediaTranscriptText").GetCurrentPattern(
        [System.Windows.Automation.ValuePattern]::Pattern).Current.Value
    if ($mp3Transcript -ne "DCx transcription fixture 42.") {
        throw "The approved FFmpeg-to-Whisper MP3 pipeline returned an unexpected transcript."
    }

    $ffmpegStatePath = Join-Path $dataRoot "local-models\ffmpeg.json"
    [IO.File]::AppendAllText($ffmpegStatePath, " ")
    Invoke-ById $localModelsWindow "RefreshLocalModelsButton" | Out-Null
    $null = Wait-For {
        $candidate = Find-ById $localModelsWindow "CurrentFfmpegBackend"
        $null -ne $candidate -and
            $candidate.Current.Name -like "Media processor: a stored approval did not match its protected per-user anchor*"
    } "A replayed or changed FFmpeg approval did not expose the accessible reapproval warning."
    $rejectedFfmpeg = $ffmpegStore.GetStateAsync([Threading.CancellationToken]::None).GetAwaiter().GetResult()
    if ($null -ne $rejectedFfmpeg.Backend -or -not $ffmpegStore.RequiresReapproval) {
        throw "A changed FFmpeg approval remained usable without explicit reapproval."
    }

    Invoke-ById $localModelsWindow "ClearFfmpegBackendButton" | Out-Null
    $null = Wait-For {
        $candidate = Find-ById $localModelsWindow "CurrentFfmpegBackend"
        $null -ne $candidate -and $candidate.Current.Name -eq "Media processor: none selected."
    } "The FFmpeg registration was not cleared."
    Set-Value $contentViewer "FilePathBox" $mp3Audio
    Invoke-ById $contentViewer "OpenFileButton" | Out-Null
    Invoke-ById $contentViewer "TranscribeWavButton" | Out-Null
    $null = Wait-For {
        $status = Find-ById $contentViewer "ContentStatusText"
        $null -ne $status -and $status.Current.Name -like "No local FFmpeg media processor is registered.*"
    } "The admitted MP3 did not fail closed after FFmpeg registration was cleared."
    $null = $store.ClearAsync([Threading.CancellationToken]::None).GetAwaiter().GetResult()
    if (-not (Test-Path -LiteralPath $fakeWhisper -PathType Leaf) -or
        -not (Test-Path -LiteralPath $ffmpegLicense -PathType Leaf)) {
        throw "Clearing FFmpeg registration deleted a source file."
    }
    $sourceFiles = @($fakeWhisper, $model, $runtimeLicense, $modelLicense)
    if (@($sourceFiles | Where-Object { -not (Test-Path -LiteralPath $_ -PathType Leaf) }).Count -ne 0) {
        throw "Clearing registration deleted a source file."
    }

    $env:DCX_DATA_ROOT = $stagingDataRoot
    $env:DCX_WHISPER_STAGING_FIXTURE = $null
    $env:DCX_WHISPER_STAGING_FIXTURE_ROOT = $null
    $deniedStagingProcess = Start-Process $exe -ArgumentList @(
        "--whisper-staging-fixture",
        $stagingArchive) -PassThru -Wait
    if ($deniedStagingProcess.ExitCode -ne 2 -or (Test-Path -LiteralPath $stagingDataRoot)) {
        throw "The synthetic runtime staging launch mode was available without its explicit fixture gate."
    }
    $env:DCX_WHISPER_STAGING_FIXTURE = "1"
    $env:DCX_WHISPER_STAGING_FIXTURE_ROOT = $runRoot
    $stagingProcess = Start-Process $exe -ArgumentList @(
        "--whisper-staging-fixture",
        $stagingArchive) -PassThru
    try {
        $stagingWindow = Wait-For {
            Find-WindowByProcessAndId $stagingProcess.Id "LocalModelsWindow"
        } "The gated Whisper runtime staging fixture window did not open."
        $null = Wait-For {
            $candidate = Find-ById $stagingWindow "DependencyCatalogSummary"
            if ($null -ne $candidate -and
                $candidate.Current.Name -like "Synthetic validation catalog dcx-p06-staging-fixture.*" -and
                $candidate.Current.Name -like "*1 generated candidate*" -and
                $candidate.Current.Name -like "*Network, download, install, pull, execution, registration, and selection are unavailable*") {
                $candidate
            }
        } "The gated synthetic staging catalog was not exposed."
        $null = Wait-For {
            $candidate = Find-ById $stagingWindow "DependencyTransferPreflight"
            if ($null -ne $candidate -and
                $candidate.Current.Name -like "Transfer preflight only. Plan SHA-256 *" -and
                $candidate.Current.Name -like "*1 candidate. 2 immutable transfer items.*" -and
                $candidate.Current.Name -like "*Redirect policy SHA-256 *Approved redirect routes: none. The reviewed policy denies every redirect.*" -and
                $candidate.Current.Name -like "*These boundaries are not connected to a transfer action in this validation window.*" -and
                $candidate.Current.Name -like "*No transfer approval exists. Nothing was downloaded, installed, extracted, pulled, registered, selected, or executed.*") {
                $candidate
            }
        } "The generated-catalog transfer preflight and no-authority notice were not exposed."
        $stagingList = Find-ById $stagingWindow "DependencyCandidatesList"
        $stagingItem = Wait-For {
            $items = $stagingList.FindAll(
                [System.Windows.Automation.TreeScope]::Descendants,
                [System.Windows.Automation.PropertyCondition]::new(
                    [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
                    [System.Windows.Automation.ControlType]::ListItem))
            @($items | Where-Object {
                $_.Current.Name -like "Synthetic Whisper runtime staging fixture.*"
            }) | Select-Object -First 1
        } "The generated runtime staging candidate was not exposed."
        $stagingItem.GetCurrentPattern(
            [System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
        Set-Value $stagingWindow "DependencyArtifactPath" $stagingArchive

        $stagingApproval = Find-ById $stagingWindow "ApproveDependencyStagingBox"
        $stagingApprovalToggle = $stagingApproval.GetCurrentPattern(
            [System.Windows.Automation.TogglePattern]::Pattern)
        if ($stagingApprovalToggle.Current.ToggleState -ne [System.Windows.Automation.ToggleState]::Off -or
            $stagingApproval.Current.IsEnabled) {
            throw "Runtime staging approval did not begin unchecked and unavailable before exact review."
        }
        $stageButton = Find-ById $stagingWindow "StageDependencyRuntimeButton"
        if ($stageButton.Current.IsEnabled) {
            throw "Runtime staging was enabled before exact review and explicit approval."
        }

        Invoke-ById $stagingWindow "ReviewDependencyArtifactButton" | Out-Null
        $null = Wait-For {
            $candidate = Find-ById $stagingWindow "DependencyArtifactResult"
            if ($null -ne $candidate -and
                $candidate.Current.Name -like "Verified synthetic-whisper-runtime.*" -and
                $candidate.Current.Name -like "*Runtime archive: 3 files and 0 directories*" -and
                $candidate.Current.Name -like "*No installation, extraction, model pull, execution, approval, or selection occurred*") {
                $candidate
            }
        } "The synthetic runtime archive did not complete exact accessible review."
        $stagingApproval = Find-ById $stagingWindow "ApproveDependencyStagingBox"
        $stagingApprovalToggle = $stagingApproval.GetCurrentPattern(
            [System.Windows.Automation.TogglePattern]::Pattern)
        $stageButton = Find-ById $stagingWindow "StageDependencyRuntimeButton"
        if (-not $stagingApproval.Current.IsEnabled -or
            $stagingApprovalToggle.Current.ToggleState -ne [System.Windows.Automation.ToggleState]::Off -or
            $stageButton.Current.IsEnabled) {
            throw "Runtime staging did not remain default-off after exact review."
        }
        $stagingActionIds = @(
            "BrowseDependencyArtifactButton",
            "ReviewDependencyArtifactButton",
            "ApproveDependencyStagingBox",
            "StageDependencyRuntimeButton")
        $stagingAccessKeys = @()
        foreach ($automationId in $stagingActionIds) {
            $action = Find-ById $stagingWindow $automationId
            if ($null -eq $action -or [string]::IsNullOrWhiteSpace($action.Current.AccessKey)) {
                throw "The $automationId staging control lacks an access key."
            }
            $stagingAccessKeys += $action.Current.AccessKey
        }
        if (@($stagingAccessKeys | Sort-Object -Unique).Count -ne $stagingAccessKeys.Count) {
            throw "Runtime staging controls expose duplicate access keys."
        }

        $stagingApprovalToggle.Toggle()
        $null = Wait-For {
            $candidate = Find-ById $stagingWindow "StageDependencyRuntimeButton"
            if ($null -ne $candidate -and $candidate.Current.IsEnabled -and
                $candidate.Current.IsKeyboardFocusable) { $candidate }
        } "The staging action did not become keyboard available after explicit approval."
        Set-Value $stagingWindow "DependencyArtifactPath" $invalidDependencyArchive
        $stagingApproval = Find-ById $stagingWindow "ApproveDependencyStagingBox"
        $stageButton = Find-ById $stagingWindow "StageDependencyRuntimeButton"
        if ($stagingApproval.GetCurrentPattern(
                [System.Windows.Automation.TogglePattern]::Pattern).Current.ToggleState -ne
            [System.Windows.Automation.ToggleState]::Off -or
            $stagingApproval.Current.IsEnabled -or $stageButton.Current.IsEnabled) {
            throw "Changing the reviewed source path did not revoke runtime staging approval."
        }
        Set-Value $stagingWindow "DependencyArtifactPath" $stagingArchive
        Invoke-ById $stagingWindow "ReviewDependencyArtifactButton" | Out-Null
        $null = Wait-For {
            $candidate = Find-ById $stagingWindow "DependencyArtifactResult"
            if ($null -ne $candidate -and $candidate.Current.Name -like "Verified synthetic-whisper-runtime.*") {
                $candidate
            }
        } "The synthetic runtime archive could not be re-reviewed after approval revocation."
        $stagingApproval = Find-ById $stagingWindow "ApproveDependencyStagingBox"
        $stagingApprovalToggle = $stagingApproval.GetCurrentPattern(
            [System.Windows.Automation.TogglePattern]::Pattern)
        $stagingApprovalToggle.Toggle()
        $stageButton = Wait-For {
            $candidate = Find-ById $stagingWindow "StageDependencyRuntimeButton"
            if ($null -ne $candidate -and $candidate.Current.IsEnabled -and
                $candidate.Current.IsKeyboardFocusable) { $candidate }
        } "The re-reviewed staging action did not become available after renewed approval."
        $stageButton.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
        $stagingResult = Wait-For {
            $candidate = Find-ById $stagingWindow "DependencyArtifactResult"
            if ($null -ne $candidate -and
                $candidate.Current.Name -like "Staged synthetic-whisper-runtime inertly.*" -and
                $candidate.Current.Name -like "*receipt identity is separately protected*" -and
                $candidate.Current.Name -like "*No installation, registration, execution, or selection occurred*") {
                $candidate
            }
        } "The explicitly approved synthetic runtime did not stage successfully."
        if (-not $stagingResult.Current.HasKeyboardFocus) {
            throw "Focus did not move to the completed staging result."
        }
        $stagingApproval = Find-ById $stagingWindow "ApproveDependencyStagingBox"
        if ($stagingApproval.GetCurrentPattern(
                [System.Windows.Automation.TogglePattern]::Pattern).Current.ToggleState -ne
            [System.Windows.Automation.ToggleState]::Off) {
            throw "Runtime staging approval was not reset after use."
        }
        $stagingCurrentWhisper = Find-ById $stagingWindow "CurrentWhisperBackend"
        if ($stagingCurrentWhisper.Current.Name -ne "Transcription: none selected.") {
            throw "Inert staging unexpectedly registered or selected a Whisper backend."
        }
    }
    finally {
        if ($null -ne $stagingProcess -and -not $stagingProcess.HasExited) {
            $null = $stagingProcess.CloseMainWindow()
            if (-not $stagingProcess.WaitForExit(5000)) { $stagingProcess.Kill($true) }
        }
        $stagingProcess = $null
        $env:DCX_DATA_ROOT = $dataRoot
        $env:DCX_WHISPER_STAGING_FIXTURE = $previousStagingFixture
        $env:DCX_WHISPER_STAGING_FIXTURE_ROOT = $previousStagingFixtureRoot
    }
    if (-not (Test-Path -LiteralPath $stagingDestination -PathType Container) -or
        -not (Test-Path -LiteralPath (Join-Path $stagingDestination "Release\whisper-cli.exe") -PathType Leaf) -or
        -not (Test-Path -LiteralPath $stagingReceipt -PathType Leaf)) {
        throw "The inert runtime staging tree or minimized receipt is incomplete."
    }
    if ((Get-FileHash -LiteralPath $stagingArchive -Algorithm SHA256).Hash.ToLowerInvariant() -ne
        $stagingArchiveDigest) {
        throw "Inert runtime staging changed the selected source archive."
    }
    $receiptText = [IO.File]::ReadAllText($stagingReceipt)
    if ($receiptText.Contains($stagingArchive, [StringComparison]::OrdinalIgnoreCase)) {
        throw "The runtime staging receipt retained the selected local source path."
    }
    $stagingRuntimeRoot = Split-Path -Parent $stagingDestination
    $unexpectedStagingState = @(
        Get-ChildItem -LiteralPath $stagingRuntimeRoot -Force |
            Where-Object { $_.FullName -cne $stagingDestination }
    )
    if ($unexpectedStagingState.Count -ne 0) {
        throw "Private archive or temporary runtime staging state was retained."
    }

    $evidence = [ordered]@{
        captured_at_utc = (Get-Date).ToUniversalTime().ToString("O")
        native_uia = "pass"
        approval_default_unchecked = $true
        save_without_approval_denied = $true
        complete_runtime_license_exposed = $true
        complete_model_license_exposed = $true
        exact_file_digests_exposed = $true
        dependency_catalog_native_uia = "pass"
        native_hardware_profile_uia = "pass"
        dependency_catalog_sha256 = "1ea3bfb04567757e0207cfd824fb5284faad2552617cbbe3330a13f66079e325"
        dependency_candidate_count = 4
        dependency_transfer_preflight_native_uia = "pass"
        dependency_transfer_plan_sha256 = "a0d997c6298fdf5f592241928c6fd9564c6de0c3e023c9eb69bd1df96406c286"
        dependency_transfer_bytes = 1281058770
        dependency_transfer_minimum_free_bytes = 3098988452
        dependency_transfer_item_count = 9
        dependency_transfer_authority_granted = $false
        dependency_transfer_action_exposed = $true
        dependency_transfer_approval_default_unchecked = $true
        dependency_transfer_start_without_approval_denied = $true
        dependency_provider_route_review_explicit = $true
        dependency_provider_route_review_head_only_notice = $true
        dependency_provider_route_review_not_invoked = $true
        dependency_staged_mismatch_denied = $true
        dependency_rejected_source_preserved = $true
        dependency_forbidden_activation_actions_exposed = $false
        dependency_runtime_staging_native_uia = "pass"
        dependency_runtime_staging_fixture_without_gate_denied = $true
        dependency_runtime_staging_approval_default_unchecked = $true
        dependency_runtime_staging_path_change_revoked_approval = $true
        dependency_runtime_staging_result_focus = $true
        dependency_runtime_staging_source_preserved = $true
        dependency_runtime_staging_receipt_source_path_retained = $false
        dependency_runtime_staging_protected_anchor = $true
        dependency_runtime_staging_registered_or_executed = $false
        keyboard_focusable_unique_access_keys = $true
        benchmark_native_uia = "pass"
        benchmark_approval_default_unchecked = $true
        benchmark_run_without_approval_denied = $true
        benchmark_exact_review_exposed = $true
        benchmark_aggregate_evidence_only = $true
        benchmark_result_focus = $true
        benchmark_path_change_revoked_approval = $true
        benchmark_cancellation_preserved_evidence = $true
        benchmark_missing_backend_unavailable = $true
        embedding_benchmark_missing_model_unavailable = $true
        executable_sha256 = $executableDigest
        model_sha256 = $modelDigest
        runtime_license_sha256 = $runtimeLicenseDigest
        model_license_sha256 = $modelLicenseDigest
        download_or_install_action_exposed = $false
        production_adapter_transcript = $transcription.Text
        accessible_viewer_wav_transcript = $viewerTranscript
        accessible_viewer_mp3_transcript = $mp3Transcript
        accessible_viewer_cancellation = $true
        canceled_partial_transcript_retained = $false
        cleared_backend_consumer_denied = $true
        ffmpeg_registration_native_uia = "pass"
        ffmpeg_approval_default_unchecked = $true
        ffmpeg_complete_license_exposed = $true
        ffmpeg_real_windows_status = "NotSigned"
        ffmpeg_save_without_approval_denied = $true
        ffmpeg_source_files_preserved = $true
        ffmpeg_whisper_mp3_pipeline = "pass"
        cleared_ffmpeg_consumer_denied = $true
        transcript_focus_after_completion = $true
        cleared_registration = $true
        source_files_preserved = $true
        approval_state_protected = $true
        whisper_reapproval_warning_uia = $true
        ffmpeg_reapproval_warning_uia = $true
        publisher_authenticity_validated = $false
        real_backend_accuracy_validated = $false
    }
    $evidence | ConvertTo-Json | Set-Content -LiteralPath $evidencePath -Encoding utf8
    "Whisper approval default unchecked=PASS"
    "Whisper save without approval denied=PASS"
    "Complete runtime/model licenses UIA=PASS"
    "Exact four-file digest review UIA=PASS"
    "Immutable dependency catalog and staged-mismatch denial UIA=PASS"
    "Native Windows hardware and resource profile UIA=PASS"
    "Exact dependency transfer footprint and capacity preflight UIA=PASS"
    "Explicit default-off inert Whisper runtime staging UIA=PASS"
    "Protected runtime staging receipt anchor composition UIA=PASS"
    "Protected Whisper/FFmpeg approval-state anchors and reapproval warnings UIA=PASS"
    "UI-approved production adapter transcript=$($transcription.Text)"
    "Approval-bound aggregate benchmark review and execution UIA=PASS"
    "Benchmark cancellation and evidence preservation UIA=PASS"
    "Accessible viewer WAV transcription and focus=PASS"
    "Accessible viewer WAV cancellation=PASS"
    "Accessible viewer approved MP3 extraction and transcription=PASS"
    "Accessible FFmpeg exact-file/license/signature registration=PASS"
    "Clear registration preserves source files=PASS"
    "Whisper download/install action absent=PASS"
    "Whisper registration evidence=$evidencePath"
}
finally {
    if ($null -ne $process -and -not $process.HasExited) {
        $null = $process.CloseMainWindow()
        if (-not $process.WaitForExit(5000)) { $process.Kill($true) }
    }
    try {
        [void][Reflection.Assembly]::LoadFrom($coreAssembly)
        $localModelStore = [DCx.Core.LocalModels.LocalModelSelectionStore]::OpenAsync(
            $dataRoot, [Threading.CancellationToken]::None).GetAwaiter().GetResult()
        $null = $localModelStore.ClearVisionAsync(
            [Threading.CancellationToken]::None).GetAwaiter().GetResult()
        $null = $localModelStore.ClearEmbeddingAsync(
            [Threading.CancellationToken]::None).GetAwaiter().GetResult()
        $whisperCleanupStore = [DCx.Core.LocalModels.WhisperBackendSelectionStore]::OpenAsync(
            $dataRoot, [Threading.CancellationToken]::None).GetAwaiter().GetResult()
        $null = $whisperCleanupStore.ClearAsync(
            [Threading.CancellationToken]::None).GetAwaiter().GetResult()
        $ffmpegCleanupStore = [DCx.Core.LocalModels.FfmpegBackendSelectionStore]::OpenAsync(
            $dataRoot, [Threading.CancellationToken]::None).GetAwaiter().GetResult()
        $null = $ffmpegCleanupStore.ClearAsync(
            [Threading.CancellationToken]::None).GetAwaiter().GetResult()
    }
    catch {
        $cleanupFailure = $_
    }
    $env:DCX_DATA_ROOT = $previousDataRoot
    $env:DCX_OFFLINE_STARTUP = $previousOfflineStartup
    $env:DCX_WHISPER_STAGING_FIXTURE = $previousStagingFixture
    $env:DCX_WHISPER_STAGING_FIXTURE_ROOT = $previousStagingFixtureRoot
    if ($null -ne $cleanupFailure) {
        throw "P06 could not revoke its disposable local-model approval state: $($cleanupFailure.Exception.GetType().Name)."
    }
}
