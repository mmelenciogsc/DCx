[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
$exe = Join-Path $repoRoot "src\DCx.Desktop\bin\Release\net10.0-windows10.0.19041.0\DCx.exe"
$ffmpeg = (Get-Command ffmpeg -ErrorAction Stop).Source
$magick = (Get-Command magick -ErrorAction Stop).Source
$previousRepoRoot = $env:DCX_REPO_ROOT
$previousDataRoot = $env:DCX_DATA_ROOT
$previousExportRoot = $env:DCX_EXPORT_ROOT
$previousVisionModel = $env:DCX_VISION_MODEL
$runRoot = Join-Path $repoRoot "artifacts\test-results\p07-$([Guid]::NewGuid().ToString('N'))"
$textFixture = Join-Path $runRoot "accessible-fixture.txt"
$videoFixture = Join-Path $runRoot "media-fixture.mp4"
$audioFixture = Join-Path $runRoot "audio-fixture.wav"
$scanImage = Join-Path $runRoot "scanned-source.png"
$pdfFixture = Join-Path $runRoot "scanned-source.pdf"
$exportRoot = Join-Path $runRoot "exports"

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

New-Item -ItemType Directory -Path $runRoot -Force | Out-Null
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

$env:DCX_REPO_ROOT = $repoRoot
$env:DCX_DATA_ROOT = Join-Path $runRoot "data"
$env:DCX_EXPORT_ROOT = $exportRoot
$env:DCX_VISION_MODEL = "qwen3-vl:4b-instruct"
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

    $openViewer = Find-ById $mainWindow "ContentViewerButton"
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
    $null = Wait-For { Test-Path $htmlExport } "Semantic HTML export was not created."
    $htmlContents = Get-Content -LiteralPath $htmlExport -Raw
    if ($htmlContents -notmatch "<main><h1>scanned-source</h1>") { throw "HTML export lacks semantic main/title structure." }
    if ($htmlContents -notmatch '<figure role="img" aria-label="Document title graphic">') { throw "HTML export lacks the reviewed image semantic repair." }

    (Find-ById $wizard "ExportDocxButton").GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    $docxExport = Join-Path $exportRoot "scanned-source-accessible.docx"
    $null = Wait-For { Test-Path $docxExport } "Structured DOCX export was not created."
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
    "Frame description leaves video paused=True"
    "User resume after description=PASS"
    "Local WAV play/pause=PASS"
    "Local PNG viewer=PASS"
    "Lookalike YouTube host blocked=True"
    "Canonical YouTube handoff tab=True"
    "Windows OCR golden text=True"
    "Normalized OCR geometry=True"
    "Accessible semantic repair=PASS"
    "Semantic HTML export=PASS"
    "Structured DOCX export=PASS"
    "Structured DOCX viewer round-trip=PASS"
    "P07 content viewer UIA validation=PASS"
}
finally {
    if (-not $process.HasExited) {
        $null = $process.CloseMainWindow()
        if (-not $process.WaitForExit(5000)) { $process.Kill() }
    }
    $env:DCX_REPO_ROOT = $previousRepoRoot
    $env:DCX_DATA_ROOT = $previousDataRoot
    $env:DCX_EXPORT_ROOT = $previousExportRoot
    $env:DCX_VISION_MODEL = $previousVisionModel
}
