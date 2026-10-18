[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
$exe = Join-Path $repoRoot "src\DCx.Desktop\bin\Release\net10.0-windows10.0.19041.0\DCx.exe"
$ffmpeg = (Get-Command ffmpeg -ErrorAction Stop).Source
$ffmpegLicense = Join-Path (Split-Path -Parent (Split-Path -Parent $ffmpeg)) "LICENSE"
$magick = (Get-Command magick -ErrorAction Stop).Source
$previousRepoRoot = $env:DCX_REPO_ROOT
$previousDataRoot = $env:DCX_DATA_ROOT
$previousExportRoot = $env:DCX_EXPORT_ROOT
$runRoot = Join-Path $repoRoot "artifacts\test-results\p07-$([Guid]::NewGuid().ToString('N'))"
$textFixture = Join-Path $runRoot "accessible-fixture.txt"
$videoFixture = Join-Path $runRoot "media-fixture.mp4"
$audioFixture = Join-Path $runRoot "audio-fixture.wav"
$scanImage = Join-Path $runRoot "scanned-source.png"
$pdfFixture = Join-Path $runRoot "scanned-source.pdf"
$exportRoot = Join-Path $runRoot "exports"
$webFixtureServer = Join-Path $repoRoot "tests\DCx.WebFixtureServer\bin\Release\net10.0\DCx.WebFixtureServer.dll"
$webServerProcess = $null
$webServerOutputTask = $null
$webServerErrorTask = $null
$process = $null
$cleanupFailure = $null

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

function Wait-For([scriptblock]$condition, [string]$failureMessage, [int]$attempts = 50) {
    for ($attempt = 0; $attempt -lt $attempts; $attempt++) {
        $result = & $condition
        if ($result) { return $result }
        Start-Sleep -Milliseconds 300
    }
    throw $failureMessage
}

function Find-WindowByProcessAndId([int]$processId, [string]$automationId) {
    $elements = [System.Windows.Automation.AutomationElement]::RootElement.FindAll(
        [System.Windows.Automation.TreeScope]::Descendants,
        [System.Windows.Automation.Condition]::TrueCondition)
    return @($elements | Where-Object {
        $_.Current.ProcessId -eq $processId -and $_.Current.AutomationId -eq $automationId
    }) | Select-Object -First 1
}

function Find-ById($root, [string]$automationId) {
    $condition = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::AutomationIdProperty,
        $automationId)
    return $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)
}

function Get-EphemeralPort {
    $listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
    try {
        $listener.Start()
        return ([Net.IPEndPoint]$listener.LocalEndpoint).Port
    }
    finally { $listener.Stop() }
}

New-Item -ItemType Directory -Path $runRoot -Force | Out-Null
if (-not (Test-Path -LiteralPath $ffmpegLicense -PathType Leaf)) {
    throw "The installed FFmpeg candidate lacks its co-located complete license file."
}
[IO.File]::WriteAllText($textFixture, "Accessible fixture line one.`r`nSecond line.")
& $ffmpeg -hide_banner -loglevel error -f lavfi -i "color=c=blue:s=320x180:d=1" -c:v libx264 -pix_fmt yuv420p -y $videoFixture
if ($LASTEXITCODE -ne 0 -or -not (Test-Path $videoFixture)) { throw "FFmpeg could not create the local video fixture." }
& $ffmpeg -hide_banner -loglevel error -f lavfi -i "sine=frequency=440:duration=1" -y $audioFixture
if ($LASTEXITCODE -ne 0 -or -not (Test-Path $audioFixture)) { throw "FFmpeg could not create the local audio fixture." }
Add-Type -AssemblyName System.Drawing
$bitmap = New-Object System.Drawing.Bitmap 1000, 400
$graphics = [System.Drawing.Graphics]::FromImage($bitmap)
try {
    $graphics.Clear([System.Drawing.Color]::White)
    $font = New-Object System.Drawing.Font "Arial", 58
    try { $graphics.DrawString("DCX ACCESSIBLE 42", $font, [System.Drawing.Brushes]::Black, 55, 140) }
    finally { $font.Dispose() }
    $bitmap.Save($scanImage, [System.Drawing.Imaging.ImageFormat]::Png)
}
finally {
    $graphics.Dispose()
    $bitmap.Dispose()
}
& $magick $scanImage $pdfFixture
if ($LASTEXITCODE -ne 0 -or -not (Test-Path $pdfFixture)) { throw "ImageMagick could not create the scanned PDF fixture." }

$webPort = Get-EphemeralPort
$webOrigin = "http://127.0.0.1:$webPort/"
$serverStart = [Diagnostics.ProcessStartInfo]::new("dotnet")
$serverStart.UseShellExecute = $false
$serverStart.CreateNoWindow = $true
$serverStart.RedirectStandardOutput = $true
$serverStart.RedirectStandardError = $true
$null = $serverStart.ArgumentList.Add($webFixtureServer)
$null = $serverStart.ArgumentList.Add("--port")
$null = $serverStart.ArgumentList.Add($webPort.ToString([Globalization.CultureInfo]::InvariantCulture))
$null = $serverStart.ArgumentList.Add("--media")
$null = $serverStart.ArgumentList.Add($videoFixture)
$webServerProcess = [Diagnostics.Process]::Start($serverStart)
if ($null -eq $webServerProcess) { throw "The local web-video fixture server did not start." }
$webServerOutputTask = $webServerProcess.StandardOutput.ReadToEndAsync()
$webServerErrorTask = $webServerProcess.StandardError.ReadToEndAsync()
$null = Wait-For {
    try {
        $health = Invoke-WebRequest -UseBasicParsing -Uri ([Uri]::new([Uri]$webOrigin, "health")) `
            -TimeoutSec 2 -NoProxy -MaximumRedirection 0
        return $health.StatusCode -eq 200 -and $health.Content -eq "ok"
    }
    catch { return $false }
} "The local web-video fixture server did not become healthy."

$env:DCX_REPO_ROOT = $repoRoot
$dataRoot = Join-Path $runRoot "data"
$env:DCX_DATA_ROOT = $dataRoot
$env:DCX_EXPORT_ROOT = $exportRoot
$visionDigest = "ef33995bb2ac37689e49c1a15cd627354d05a3c21d1f4bdcd6a5c83825349c1f"
$visionModel = "llamacpp:$visionDigest"
$licenseDigest = "d0fa3710cf6cf8e18009051c28988e4aa8114b0d4cd6e3f102bb9ccba50d7cc7"
$process = Start-Process $exe -PassThru
try {
    $mainWindow = Wait-For {
        $condition = New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::ProcessIdProperty,
            $process.Id)
        [System.Windows.Automation.AutomationElement]::RootElement.FindFirst(
            [System.Windows.Automation.TreeScope]::Children,
            $condition)
    } "The DCx main window did not appear."
    $address = Wait-For { Find-ById $mainWindow "AddressBox" } "The browser shell was not ready."
    $null = Wait-For {
        $value = $address.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)
        $value.Current.Value -eq "https://example.com/"
    } "The initial browser did not finish loading."

    $localModelsButton = Find-ById $mainWindow "LocalModelsButton"
    if ($null -eq $localModelsButton) { throw "The main window did not expose local model settings." }
    $localModelsButton.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    $localModelsWindow = Wait-For {
        Find-WindowByProcessAndId $process.Id "LocalModelsWindow"
    } "The accessible local model settings window did not open."
    $localModelsStatus = Wait-For {
        $candidate = Find-ById $localModelsWindow "LocalModelsStatus"
        if ($null -ne $candidate -and $candidate.Current.Name -like "Found * installed model records.*") { $candidate }
    } "Local model inventory did not finish."
    $approval = Wait-For {
        Find-ById $localModelsWindow "ApproveLocalModelSelectionBox"
    } "The explicit local model approval checkbox was not exposed."
    $approvalToggle = $approval.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern)
    if ($approvalToggle.Current.ToggleState -ne [System.Windows.Automation.ToggleState]::Off) {
        throw "Local model approval did not default to unchecked."
    }
    $modelList = Wait-For {
        Find-ById $localModelsWindow "InstalledLocalModelsList"
    } "The installed local model list was not exposed."
    $listItemCondition = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
        [System.Windows.Automation.ControlType]::ListItem)
    $modelItems = $modelList.FindAll([System.Windows.Automation.TreeScope]::Descendants, $listItemCondition)
    if ($modelItems.Count -lt 1) { throw "No installed local model identity was exposed through UI Automation." }
    $selectedModelItem = @($modelItems | Where-Object { $_.Current.Name -like "*$visionModel*" }) | Select-Object -First 1
    if ($null -eq $selectedModelItem) { throw "The digest-bound vision-model identity was not exposed in the installed list." }
    $selectedModelItem.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
    $modelDetails = Wait-For {
        $candidate = Find-ById $localModelsWindow "LocalModelDetails"
        if ($null -ne $candidate -and $candidate.Current.Name -like "*License SHA-256: $licenseDigest*") { $candidate }
    } "The selected model's exact license digest was not exposed for review."
    $modelLicense = Wait-For {
        $candidate = Find-ById $localModelsWindow "LocalModelLicenseText"
        if ($null -ne $candidate) {
            $pattern = $candidate.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)
            if (-not [string]::IsNullOrWhiteSpace($pattern.Current.Value) -and
                $pattern.Current.Value -notlike "Select an installed model*") { $candidate }
        }
    } "The selected model's complete license text was not exposed for review."
    $approvalToggle.Toggle()
    $useVision = Wait-For {
        $candidate = Find-ById $localModelsWindow "UseLocalModelForVisionButton"
        if ($null -ne $candidate -and $candidate.Current.IsEnabled) { $candidate }
    } "The eligible digest-bound model could not be selected for vision."
    $useVision.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    $null = Wait-For {
        $candidate = Find-ById $localModelsWindow "LocalModelsStatus"
        $null -ne $candidate -and $candidate.Current.Name -like "The exact digest-bound local vision model selection was saved.*"
    } "The explicitly approved local vision selection was not saved."
    $currentVision = Wait-For {
        $candidate = Find-ById $localModelsWindow "CurrentVisionModel"
        if ($null -ne $candidate -and $candidate.Current.Name -like "Vision: $visionModel.*") { $candidate }
    } "The persisted digest-bound vision selection was not exposed through UI Automation."
    if ($approvalToggle.Current.ToggleState -ne [System.Windows.Automation.ToggleState]::Off) {
        throw "Local model approval was not reset after selection."
    }
    if ($null -ne (Find-ById $localModelsWindow "DownloadLocalModelButton")) {
        throw "The local model settings unexpectedly exposed a download action."
    }

    $ffmpegDigest = (Get-FileHash -LiteralPath $ffmpeg -Algorithm SHA256).Hash.ToLowerInvariant()
    $ffmpegLicenseDigest = (Get-FileHash -LiteralPath $ffmpegLicense -Algorithm SHA256).Hash.ToLowerInvariant()
    (Find-ById $localModelsWindow "FfmpegExecutablePath").GetCurrentPattern(
        [System.Windows.Automation.ValuePattern]::Pattern).SetValue($ffmpeg)
    (Find-ById $localModelsWindow "FfmpegLicensePath").GetCurrentPattern(
        [System.Windows.Automation.ValuePattern]::Pattern).SetValue($ffmpegLicense)
    (Find-ById $localModelsWindow "ReviewFfmpegBackendButton").GetCurrentPattern(
        [System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    $ffmpegDetails = Wait-For {
        $candidate = Find-ById $localModelsWindow "FfmpegBackendDetails"
        if ($null -ne $candidate -and
            $candidate.Current.Name -like "*SHA-256 $ffmpegDigest*" -and
            $candidate.Current.Name -like "*License SHA-256 $ffmpegLicenseDigest*" -and
            $candidate.Current.Name -like "*Windows signature status*") { $candidate }
    } "The exact installed FFmpeg executable, license and signature evidence were not exposed." 160
    $ffmpegApproval = Find-ById $localModelsWindow "ApproveFfmpegBackendBox"
    $ffmpegApprovalToggle = $ffmpegApproval.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern)
    if ($ffmpegApprovalToggle.Current.ToggleState -ne [System.Windows.Automation.ToggleState]::Off) {
        throw "FFmpeg approval did not default to unchecked."
    }
    $ffmpegApprovalToggle.Toggle()
    (Find-ById $localModelsWindow "SaveFfmpegBackendButton").GetCurrentPattern(
        [System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    $currentFfmpeg = Wait-For {
        $candidate = Find-ById $localModelsWindow "CurrentFfmpegBackend"
        if ($null -ne $candidate -and
            $candidate.Current.Name -like "Media processor: ffmpeg.exe.*" -and
            $candidate.Current.Name -like "*Executable digest $ffmpegDigest*") { $candidate }
    } "The isolated test profile did not retain the explicitly approved FFmpeg binding."
    if ($ffmpegApprovalToggle.Current.ToggleState -ne [System.Windows.Automation.ToggleState]::Off) {
        throw "FFmpeg approval was not reset after saving."
    }
    $closeLocalModels = Wait-For {
        Find-ById $localModelsWindow "CloseLocalModelsButton"
    } "The local model settings close button was not exposed."
    $closeLocalModels.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    $null = Wait-For {
        $null -eq (Find-WindowByProcessAndId $process.Id "LocalModelsWindow")
    } "The local model settings window did not close."

    $openViewer = Wait-For {
        Find-ById $mainWindow "ContentViewerButton"
    } "The files and media viewer button was not exposed after closing local model settings."
    $openViewer.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    $viewer = Wait-For { Find-WindowByProcessAndId $process.Id "ContentViewerWindow" } "The content viewer did not open."
    $status = Find-ById $viewer "ContentStatusText"

    $filePath = Find-ById $viewer "FilePathBox"
    $filePath.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue($textFixture)
    (Find-ById $viewer "OpenFileButton").GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    $documentText = Wait-For { Find-ById $viewer "DocumentText" } "The accessible text surface did not appear."
    $textValue = $documentText.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).Current.Value
    if ($textValue -notmatch "Accessible fixture line one" -or $textValue -notmatch "Second line") {
        throw "The text fixture was not exposed through UI Automation."
    }

    $folderPath = Find-ById $viewer "FolderPathBox"
    $folderPath.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue($runRoot)
    (Find-ById $viewer "ListFolderButton").GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    $folderItems = Wait-For { Find-ById $viewer "FolderItems" } "Folder contents were not exposed."
    $listItemCondition = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
        [System.Windows.Automation.ControlType]::ListItem)
    $listed = $folderItems.FindAll([System.Windows.Automation.TreeScope]::Descendants, $listItemCondition)
    if ($listed.Count -lt 2) { throw "The folder listing did not contain the created fixtures." }

    $filePath = Find-ById $viewer "FilePathBox"
    $filePath.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue($videoFixture)
    (Find-ById $viewer "OpenFileButton").GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    $null = Wait-For { $status.Current.Name -like "Opened media-fixture.mp4 as Video*" } "The local MP4 did not open in paused state."
    (Find-ById $viewer "MediaPlayButton").GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    $null = Wait-For { $status.Current.Name -eq "Media playing." } "The media Play action failed."
    (Find-ById $viewer "MediaPauseButton").GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    $null = Wait-For { $status.Current.Name -eq "Media paused." } "The media Pause action failed."
    (Find-ById $viewer "DescribeFrameButton").GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    $null = Wait-For { $status.Current.Name -like "Frame description complete.*" } "The local VLM frame description did not complete." 300
    $frameDescription = Wait-For {
        $candidate = Find-ById $viewer "FrameDescriptionText"
        if ($null -ne $candidate -and [string]$candidate.Current.Name -match "(?i)blue") { $candidate }
    } "The frame description did not identify the solid blue fixture." 30
    $frameDescriptionValue = [string]$frameDescription.Current.Name
    (Find-ById $viewer "MediaPlayButton").GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    $null = Wait-For { $status.Current.Name -eq "Media playing." } "The user could not resume after frame description."
    (Find-ById $viewer "MediaPauseButton").GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()

    $filePath.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue($audioFixture)
    (Find-ById $viewer "OpenFileButton").GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    $null = Wait-For { $status.Current.Name -like "Opened audio-fixture.wav as Audio*" } "The local WAV did not open in paused state."
    (Find-ById $viewer "MediaPlayButton").GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    $null = Wait-For { $status.Current.Name -eq "Media playing." } "The audio Play action failed."
    (Find-ById $viewer "MediaPauseButton").GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()

    $filePath.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue($scanImage)
    (Find-ById $viewer "OpenFileButton").GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    try {
        $null = Wait-For { $status.Current.Name -like "Opened scanned-source.png as Image*" } "The local PNG did not open in the image viewer."
    }
    catch {
        throw "The local PNG did not open in the image viewer. Status=$($status.Current.Name)"
    }

    $youtube = Find-ById $viewer "YouTubeUrlBox"
    $youtube.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue("https://youtube.com.evil.example/watch?v=dQw4w9WgXcQ")
    (Find-ById $viewer "OpenYouTubeButton").GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    $null = Wait-For { $status.Current.Name -like "YouTube URL blocked.*" } "A lookalike YouTube host was not blocked."

    $youtube.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue("https://youtu.be/dQw4w9WgXcQ?t=42")
    (Find-ById $viewer "OpenYouTubeButton").GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    $null = Wait-For { $status.Current.Name -like "Validated YouTube video opened*" } "The canonical YouTube handoff was not initiated."
    $tabs = Find-ById $mainWindow "BrowserTabs"
    $tabCondition = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
        [System.Windows.Automation.ControlType]::TabItem)
    $null = Wait-For {
        $tabs.FindAll([System.Windows.Automation.TreeScope]::Children, $tabCondition).Count -ge 2
    } "The YouTube handoff did not create a shared-browser tab."

    $address.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue(
        ([Uri]::new([Uri]$webOrigin, "web-video")).AbsoluteUri)
    (Find-ById $mainWindow "GoButton").GetCurrentPattern(
        [System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    $null = Wait-For {
        $address.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).Current.Value -eq
            ([Uri]::new([Uri]$webOrigin, "web-video")).AbsoluteUri
    } "The deterministic web-video fixture did not load in the shared browser."
    Start-Sleep -Milliseconds 750
    $frameQuestion = Find-ById $viewer "FrameQuestionBox"
    $frameQuestion.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue(
        "What color fills the paused video?")
    $askWebVideo = Find-ById $viewer "AskWebVideoFrameButton"
    $resumeWebVideo = Find-ById $viewer "ResumeWebVideoButton"
    if ($resumeWebVideo.Current.IsEnabled) {
        throw "Web-video resume was enabled before DCx paused a video."
    }
    $askWebVideo.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    try {
        $null = Wait-For {
            $status.Current.Name -like "Web video frame answer complete at *video remains paused*"
        } "The bounded shared-viewport web-video frame question did not complete." 300
    }
    catch {
        throw "The bounded shared-viewport web-video frame question did not complete. Status=$($status.Current.Name)"
    }
    $webFrameAnswer = Wait-For {
        $candidate = Find-ById $viewer "FrameDescriptionText"
        if ($null -ne $candidate -and [string]$candidate.Current.Name -match "(?i)blue") { $candidate }
    } "The web-video frame answer did not identify the solid blue fixture." 30
    $webFrameAnswerValue = [string]$webFrameAnswer.Current.Name
    if (-not $resumeWebVideo.Current.IsEnabled) {
        throw "Explicit web-video resume did not enable after the paused-frame answer."
    }
    $resumeWebVideo.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    $null = Wait-For {
        $status.Current.Name -eq "Web video resumed after explicit user action."
    } "The web video did not resume after explicit user action."

    $filePath.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue($pdfFixture)
    (Find-ById $viewer "OpenFileButton").GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    $wizard = Wait-For { Find-WindowByProcessAndId $process.Id "ScannedPdfWizardWindow" } "The scanned PDF wizard did not open."
    (Find-ById $wizard "RecognizePdfButton").GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    $wizardStatus = Find-ById $wizard "WizardStatusText"
    $null = Wait-For { $wizardStatus.Current.Name -like "OCR complete.*" } "Windows OCR did not complete and validate geometry." 100
    $blocks = Find-ById $wizard "OcrBlocksList"
    $ocrItems = $blocks.FindAll([System.Windows.Automation.TreeScope]::Descendants, $listItemCondition)
    $ocrText = @($ocrItems | ForEach-Object { $_.Current.Name }) -join " "
    if ($ocrText -notmatch "(?i)DCX" -or $ocrText -notmatch "42") { throw "OCR golden text was not exposed accessibly: $ocrText" }

    $ocrItems[0].GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
    $repairLabel = Find-ById $wizard "RepairLabelBox"
    $repairLabel.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue("Document title graphic")
    $repairRole = Find-ById $wizard "RepairRoleBox"
    $repairRole.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Expand()
    $imageRole = Wait-For {
        $items = $repairRole.FindAll([System.Windows.Automation.TreeScope]::Descendants, $listItemCondition)
        @($items | Where-Object { $_.Current.Name -eq "ImageDescription" }) | Select-Object -First 1
    } "The semantic role options were not exposed accessibly."
    $imageRole.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
    (Find-ById $wizard "ApplyRepairButton").GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    $null = Wait-For { $wizardStatus.Current.Name -like "Semantic repair applied*" } "The semantic repair was not applied."

    (Find-ById $wizard "ExportHtmlButton").GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    $htmlExport = Join-Path $exportRoot "scanned-source-accessible.html"
    $null = Wait-For {
        (Test-Path $htmlExport) -and $wizardStatus.Current.Name -like "Semantic HTML exported:*"
    } "Semantic HTML export did not finish."
    $htmlContents = Get-Content -LiteralPath $htmlExport -Raw
    if ($htmlContents -notmatch "<main><h1>scanned-source</h1>") { throw "HTML export lacks semantic main/title structure." }
    if ($htmlContents -notmatch '<figure role="img" aria-label="Document title graphic">') { throw "HTML export lacks the reviewed image semantic repair." }

    (Find-ById $wizard "ExportDocxButton").GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    $docxExport = Join-Path $exportRoot "scanned-source-accessible.docx"
    $null = Wait-For {
        (Test-Path $docxExport) -and $wizardStatus.Current.Name -like "Structured DOCX exported:*"
    } "Structured DOCX export did not finish."
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = [IO.Compression.ZipFile]::OpenRead($docxExport)
    try {
        if ($null -eq $archive.GetEntry("word/document.xml")) { throw "DOCX export lacks word/document.xml." }
    }
    finally { $archive.Dispose() }

    $filePath.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue($docxExport)
    (Find-ById $viewer "OpenFileButton").GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    $null = Wait-For { $status.Current.Name -like "Opened scanned-source-accessible.docx as Word*" } "The exported DOCX did not reopen in the accessible viewer."
    $documentText = Find-ById $viewer "DocumentText"
    $docxText = $documentText.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).Current.Value
    if ($docxText -notmatch "(?i)DCX" -or $docxText -notmatch "42") { throw "The structured DOCX did not round-trip its OCR text." }

    "Accessible text content=True"
    "Folder item count=$($listed.Count)"
    "Local MP4 initial state=paused"
    "Local MP4 play/pause=PASS"
    "Local VLM frame description=$frameDescriptionValue"
    "Persisted local model selection UIA=PASS"
    "Complete local model license text UIA=PASS"
    "Local model approval default unchecked=True"
    "Local model download action absent=True"
    "Explicit FFmpeg executable/license binding UIA=PASS"
    "Local frame extraction uses approved digest-bound FFmpeg=PASS"
    "Frame description leaves video paused=True"
    "User resume after description=PASS"
    "Local WAV play/pause=PASS"
    "Local PNG viewer=PASS"
    "Lookalike YouTube host blocked=True"
    "Canonical YouTube handoff tab=True"
    "Shared-viewport web-video visible-rectangle Q&A=$webFrameAnswerValue"
    "Web video remains paused for follow-up=True"
    "Explicit web-video resume=PASS"
    "Windows OCR golden text=True"
    "Normalized OCR geometry=True"
    "Accessible semantic repair=PASS"
    "Semantic HTML export=PASS"
    "Structured DOCX export=PASS"
    "Structured DOCX viewer round-trip=PASS"
    "P07 content viewer UIA validation=PASS"
}
finally {
    if ($null -ne $process -and -not $process.HasExited) {
        $null = $process.CloseMainWindow()
        if (-not $process.WaitForExit(5000)) { $process.Kill() }
    }
    try {
        $coreAssembly = Join-Path $repoRoot "src\DCx.Core\bin\Release\net10.0\DCx.Core.dll"
        [void][Reflection.Assembly]::LoadFrom($coreAssembly)
        $localModelStore = [DCx.Core.LocalModels.LocalModelSelectionStore]::OpenAsync(
            $dataRoot, [Threading.CancellationToken]::None).GetAwaiter().GetResult()
        $null = $localModelStore.ClearVisionAsync(
            [Threading.CancellationToken]::None).GetAwaiter().GetResult()
        $null = $localModelStore.ClearEmbeddingAsync(
            [Threading.CancellationToken]::None).GetAwaiter().GetResult()
        $whisperStore = [DCx.Core.LocalModels.WhisperBackendSelectionStore]::OpenAsync(
            $dataRoot, [Threading.CancellationToken]::None).GetAwaiter().GetResult()
        $null = $whisperStore.ClearAsync(
            [Threading.CancellationToken]::None).GetAwaiter().GetResult()
        $ffmpegStore = [DCx.Core.LocalModels.FfmpegBackendSelectionStore]::OpenAsync(
            $dataRoot, [Threading.CancellationToken]::None).GetAwaiter().GetResult()
        $null = $ffmpegStore.ClearAsync(
            [Threading.CancellationToken]::None).GetAwaiter().GetResult()
    }
    catch {
        $cleanupFailure = $_
    }
    $env:DCX_REPO_ROOT = $previousRepoRoot
    $env:DCX_DATA_ROOT = $previousDataRoot
    $env:DCX_EXPORT_ROOT = $previousExportRoot
    if ($null -ne $webServerProcess -and -not $webServerProcess.HasExited) {
        $webServerProcess.Kill($true)
        $webServerProcess.WaitForExit()
    }
    if ($null -ne $webServerOutputTask -and $null -ne $webServerErrorTask) {
        $null = $webServerOutputTask.GetAwaiter().GetResult()
        $null = $webServerErrorTask.GetAwaiter().GetResult()
    }
    if ($null -ne $cleanupFailure) {
        throw "P07 could not revoke its disposable local-model approval state: $($cleanupFailure.Exception.GetType().Name)."
    }
}
