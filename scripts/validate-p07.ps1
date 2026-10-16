[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
$exe = Join-Path $repoRoot "src\DCx.Desktop\bin\Release\net10.0-windows10.0.19041.0\DCx.exe"
$ffmpeg = (Get-Command ffmpeg -ErrorAction Stop).Source
$previousRepoRoot = $env:DCX_REPO_ROOT
$previousDataRoot = $env:DCX_DATA_ROOT
$runRoot = Join-Path $repoRoot "artifacts\test-results\p07-$([Guid]::NewGuid().ToString('N'))"
$textFixture = Join-Path $runRoot "accessible-fixture.txt"
$videoFixture = Join-Path $runRoot "media-fixture.mp4"

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

$env:DCX_REPO_ROOT = $repoRoot
$env:DCX_DATA_ROOT = Join-Path $runRoot "data"
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

    "Accessible text content=True"
    "Folder item count=$($listed.Count)"
    "Local MP4 initial state=paused"
    "Local MP4 play/pause=PASS"
    "Lookalike YouTube host blocked=True"
    "Canonical YouTube handoff tab=True"
    "P07 content viewer UIA validation=PASS"
}
finally {
    if (-not $process.HasExited) {
        $null = $process.CloseMainWindow()
        if (-not $process.WaitForExit(5000)) { $process.Kill() }
    }
    $env:DCX_REPO_ROOT = $previousRepoRoot
    $env:DCX_DATA_ROOT = $previousDataRoot
}
