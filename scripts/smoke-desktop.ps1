[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
$exe = Join-Path $repoRoot "src\DCx.Desktop\bin\Release\net10.0-windows10.0.19041.0\DCx.exe"
$requiredNames = @("Go back", "Web address", "Navigate to web address", "Browser status")

if (-not (Test-Path $exe)) {
    throw "Build the Release configuration before running the desktop smoke test."
}

$process = Start-Process $exe -PassThru
try {
    for ($attempt = 0; $attempt -lt 20 -and $process.MainWindowHandle -eq 0; $attempt++) {
        Start-Sleep -Milliseconds 500
        $process.Refresh()
    }

    Add-Type -AssemblyName UIAutomationClient
    Add-Type -AssemblyName UIAutomationTypes
    $condition = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ProcessIdProperty,
        $process.Id)
    $window = [System.Windows.Automation.AutomationElement]::RootElement.FindFirst(
        [System.Windows.Automation.TreeScope]::Children,
        $condition)

    if ($null -eq $window) {
        throw "The DCx main window was not exposed through UI Automation."
    }

    $nodes = $window.FindAll(
        [System.Windows.Automation.TreeScope]::Descendants,
        [System.Windows.Automation.Condition]::TrueCondition)
    $names = @($nodes | ForEach-Object { $_.Current.Name })

    foreach ($requiredName in $requiredNames) {
        if ($requiredName -notin $names) {
            throw "Required UI Automation element '$requiredName' was not found."
        }
    }

    $webViewFound = $false
    foreach ($node in $nodes) {
        if ($node.Current.Name -like "Interactive web page, * profile" -and
            $node.Current.AutomationId -like "WebView-*") {
            $webViewFound = $true
            break
        }
    }
    if (-not $webViewFound) {
        throw "A profile-specific interactive WebView UI Automation element was not found."
    }

    "PID=$($process.Id)"
    "Title=$($process.MainWindowTitle)"
    "UIA required elements=$($requiredNames.Count + 1)/$($requiredNames.Count + 1)"
}
finally {
    if (-not $process.HasExited) {
        $null = $process.CloseMainWindow()
        if (-not $process.WaitForExit(5000)) {
            $process.Kill()
        }
    }
}
