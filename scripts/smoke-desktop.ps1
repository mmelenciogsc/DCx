[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
$exe = Join-Path $repoRoot "src\DCx.Desktop\bin\Release\net10.0-windows10.0.19041.0\DCx.exe"
$requiredNames = @("Go back", "Web address", "Navigate to web address")

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

    $surfaceReady = $false
    for ($attempt = 0; $attempt -lt 40 -and -not $surfaceReady; $attempt++) {
        $nodes = $window.FindAll(
            [System.Windows.Automation.TreeScope]::Descendants,
            [System.Windows.Automation.Condition]::TrueCondition)
        $names = @($nodes | ForEach-Object { $_.Current.Name })
        $requiredControlsFound = @($requiredNames | Where-Object { $_ -in $names }).Count -eq $requiredNames.Count
        $statusFound = @($nodes | Where-Object { $_.Current.AutomationId -eq "StatusText" }).Count -eq 1
        $webViewFound = @($nodes | Where-Object {
            $_.Current.Name -like "Interactive web page, * profile" -and
            $_.Current.AutomationId -like "WebView-*"
        }).Count -ge 1
        $surfaceReady = $requiredControlsFound -and $statusFound -and $webViewFound
        if (-not $surfaceReady) { Start-Sleep -Milliseconds 250 }
    }
    if (-not $surfaceReady) {
        throw "The complete DCx browser UI Automation surface was not ready within the timeout."
    }

    "PID=$($process.Id)"
    "Title=$($process.MainWindowTitle)"
    "UIA required elements=$($requiredNames.Count + 2)/$($requiredNames.Count + 2)"
}
finally {
    if (-not $process.HasExited) {
        $null = $process.CloseMainWindow()
        if (-not $process.WaitForExit(5000)) {
            $process.Kill()
        }
    }
}
