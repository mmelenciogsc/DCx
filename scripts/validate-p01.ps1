[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
$exe = Join-Path $repoRoot "src\DCx.Desktop\bin\Release\net10.0-windows10.0.19041.0\DCx.exe"
$fixtureDll = Join-Path $repoRoot "tests\DCx.WebFixtureServer\bin\Release\net10.0\DCx.WebFixtureServer.dll"
$runRoot = Join-Path $repoRoot "artifacts\test-results\p01-$([Guid]::NewGuid().ToString('N'))"
$downloadRoot = Join-Path $runRoot "downloads"
$dataRoot = Join-Path $runRoot "data"
$fixturePort = 43187
$fixtureBase = "http://127.0.0.1:$fixturePort"
$previousDataRoot = $env:DCX_DATA_ROOT
$previousDownloadRoot = $env:DCX_DOWNLOAD_ROOT

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

function Get-MainWindow([int]$processId) {
    $condition = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ProcessIdProperty,
        $processId)
    return [System.Windows.Automation.AutomationElement]::RootElement.FindFirst(
        [System.Windows.Automation.TreeScope]::Children,
        $condition)
}

function Find-ById($root, [string]$automationId) {
    $condition = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::AutomationIdProperty,
        $automationId)
    return $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)
}

function Wait-For([scriptblock]$condition, [string]$failureMessage, [int]$attempts = 30) {
    for ($attempt = 0; $attempt -lt $attempts; $attempt++) {
        $result = & $condition
        if ($result) {
            return $result
        }
        Start-Sleep -Milliseconds 500
    }
    throw $failureMessage
}

if (-not (Test-Path $exe)) {
    throw "Build the Release configuration before running P01 validation."
}
if (-not (Test-Path $fixtureDll)) {
    throw "Build the controlled web fixture server before running P01 validation."
}

$env:DCX_DATA_ROOT = $dataRoot
$env:DCX_DOWNLOAD_ROOT = $downloadRoot
$fixtureProcess = Start-Process "dotnet.exe" -ArgumentList @($fixtureDll, "--port", $fixturePort) -WindowStyle Hidden -PassThru
$process = $null
try {
    $null = Wait-For {
        try {
            (Invoke-WebRequest "$fixtureBase/health" -UseBasicParsing -TimeoutSec 2).Content -eq "ok"
        }
        catch {
            $null
        }
    } "The controlled browser fixture server did not start."

    $process = Start-Process $exe -PassThru
    $window = Wait-For { Get-MainWindow $process.Id } "The DCx main window did not appear."
    $address = Wait-For { Find-ById $window "AddressBox" } "The address field was not exposed through UIA."
    $newTab = Find-ById $window "NewTabButton"
    $profiles = Find-ById $window "ProfilesBox"
    $agentButton = Find-ById $window "AgentDrawerButton"
    $browserTabs = Find-ById $window "BrowserTabs"
    $bookmarkButton = Find-ById $window "BookmarkButton"
    $bookmarksButton = Find-ById $window "BookmarksButton"
    $historyButton = Find-ById $window "HistoryButton"
    $downloadsButton = Find-ById $window "DownloadsButton"
    $goButton = Find-ById $window "GoButton"
    $listItemCondition = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
        [System.Windows.Automation.ControlType]::ListItem)
    $tabItemCondition = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
        [System.Windows.Automation.ControlType]::TabItem)

    foreach ($required in @(
        $newTab,
        $profiles,
        $agentButton,
        $browserTabs,
        $bookmarkButton,
        $bookmarksButton,
        $historyButton,
        $downloadsButton,
        $goButton)) {
        if ($null -eq $required) {
            throw "A required P01 browser-shell control was not exposed through UIA."
        }
    }

    $httpsLoaded = Wait-For {
        $valuePattern = $address.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)
        $valuePattern.Current.Value -eq "https://example.com/"
    } "The live browser did not load the HTTPS fixture."

    $keys = New-Object -ComObject WScript.Shell
    $null = $keys.AppActivate($process.Id)
    $browserTabs.SetFocus()
    $keys.SendKeys("^l")
    $null = Wait-For { $address.Current.HasKeyboardFocus } "Control L did not place keyboard focus in the address field."
    $keys.SendKeys("^t")
    $null = Wait-For {
        $tabItems = $browserTabs.FindAll(
            [System.Windows.Automation.TreeScope]::Descendants,
            $tabItemCondition)
        $tabItems.Count -ge 2
    } "Control T did not open an accessible browser tab."
    $keys.SendKeys("^w")
    $null = Wait-For {
        $tabItems = $browserTabs.FindAll(
            [System.Windows.Automation.TreeScope]::Descendants,
            $tabItemCondition)
        $tabItems.Count -eq 1
    } "Control W did not close the current browser tab."

    $invoke = $newTab.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)
    $profileExpand = $profiles.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePatternIdentifiers]::Pattern)
    $profileExpand.Expand()
    $workItem = Wait-For {
        $desktopNodes = [System.Windows.Automation.AutomationElement]::RootElement.FindAll(
            [System.Windows.Automation.TreeScope]::Descendants,
            [System.Windows.Automation.Condition]::TrueCondition)
        @($desktopNodes | Where-Object {
            $_.Current.ProcessId -eq $process.Id -and
            $_.Current.ControlType -eq [System.Windows.Automation.ControlType]::ListItem -and
            $_.Current.Name -eq "Work" -and
            @($_.GetSupportedPatterns() | Where-Object {
                $_ -eq [System.Windows.Automation.SelectionItemPatternIdentifiers]::Pattern
            }).Count -eq 1
        }) | Select-Object -First 1
    } "The Work browser profile option was not exposed through UIA."
    $workItem.GetCurrentPattern([System.Windows.Automation.SelectionItemPatternIdentifiers]::Pattern).Select()
    $profileExpand.Collapse()
    $invoke.Invoke()
    $secondTab = Wait-For {
        $tabs = $browserTabs.FindAll(
            [System.Windows.Automation.TreeScope]::Descendants,
            $tabItemCondition)
        if ($tabs.Count -ge 2) { $tabs.Count } else { $null }
    } "New Tab did not create a second accessible tab."
    $null = Wait-For {
        $nodes = $browserTabs.FindAll(
            [System.Windows.Automation.TreeScope]::Descendants,
            [System.Windows.Automation.Condition]::TrueCondition)
        @($nodes | Where-Object {
            $_.Current.ControlType -eq [System.Windows.Automation.ControlType]::TabItem -and
            $_.Current.Name -eq "Example Domain, Work profile"
        }).Count -eq 1
    } "The Work-profile tab did not finish loading the HTTPS fixture."

    $agentButton.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    $agentDrawer = Wait-For { Find-ById $window "AgentDrawer" } "The accessible agent drawer did not open."
    if ($agentDrawer.Current.Name -ne "Agent controls") {
        throw "The agent drawer has an unexpected accessible name."
    }

    $closeAgent = $window.FindFirst(
        [System.Windows.Automation.TreeScope]::Descendants,
        (New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::NameProperty,
            "Close agent controls")))
    $closeAgent.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()

    $bookmarkButton.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    $bookmarksButton.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    $bookmarksList = Wait-For { Find-ById $window "BookmarksList" } "The bookmarks list did not open."
    $bookmarkCount = Wait-For {
        $items = $bookmarksList.FindAll(
            [System.Windows.Automation.TreeScope]::Descendants,
            $listItemCondition)
        if ($items.Count -ge 1) { $items.Count } else { $null }
    } "The current HTTPS page was not added to accessible bookmarks."

    $historyButton.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    $historyList = Wait-For { Find-ById $window "HistoryList" } "The history list did not open."
    $historyCount = Wait-For {
        $items = $historyList.FindAll(
            [System.Windows.Automation.TreeScope]::Descendants,
            $listItemCondition)
        if ($items.Count -ge 1) { $items.Count } else { $null }
    } "The loaded HTTPS page was not added to accessible history."

    $address.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue("$fixtureBase/")
    $goButton.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    $fixtureInput = Wait-For {
        $nodes = $window.FindAll(
            [System.Windows.Automation.TreeScope]::Descendants,
            [System.Windows.Automation.Condition]::TrueCondition)
        @($nodes | Where-Object {
            $_.Current.ControlType -eq [System.Windows.Automation.ControlType]::Edit -and
            $_.Current.Name -eq "Approval code"
        }) | Select-Object -First 1
    } "The live WebView did not expose the labeled manual-control fixture field."
    $fixtureValue = $fixtureInput.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)
    $fixtureValue.SetValue("DCx manual control fixture")
    $null = Wait-For {
        $nodes = $window.FindAll(
            [System.Windows.Automation.TreeScope]::Descendants,
            [System.Windows.Automation.Condition]::TrueCondition)
        $currentInput = @($nodes | Where-Object {
            $_.Current.ControlType -eq [System.Windows.Automation.ControlType]::Edit -and
            $_.Current.Name -eq "Approval code"
        }) | Select-Object -First 1
        if ($null -eq $currentInput) { return $null }
        $currentValue = $currentInput.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)
        $currentValue.Current.Value -eq "DCx manual control fixture"
    } "Direct interaction with the live page field did not succeed."

    $address.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue("$fixtureBase/download")
    $goButton.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    $downloadPath = Join-Path $downloadRoot "dcx-fixture.txt"
    $null = Wait-For { Test-Path $downloadPath } "The controlled download was not saved to the isolated test directory."
    $downloadsButton.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    $downloadsList = Wait-For { Find-ById $window "DownloadsList" } "The downloads list did not open."
    $downloadCount = Wait-For {
        $items = $downloadsList.FindAll(
            [System.Windows.Automation.TreeScope]::Descendants,
            $listItemCondition)
        if ($items.Count -ge 1) { $items.Count } else { $null }
    } "The completed download was not exposed in the accessible downloads list."

    $profileName = $profiles.Current.Name
    $allNodes = $window.FindAll(
        [System.Windows.Automation.TreeScope]::Descendants,
        [System.Windows.Automation.Condition]::TrueCondition)
    $accessibleTabs = @($allNodes | Where-Object {
        $_.Current.ControlType -eq [System.Windows.Automation.ControlType]::TabItem -and
        $_.Current.Name -like "*, * profile"
    })
    $accessibleTabNames = @($accessibleTabs | ForEach-Object { $_.Current.Name })
    $hasDefaultProfile = @($accessibleTabNames | Where-Object { $_ -like "*, Default profile" }).Count -ge 1
    $hasWorkProfile = @($accessibleTabNames | Where-Object { $_ -like "*, Work profile" }).Count -ge 1
    if ($accessibleTabs.Count -lt 2 -or -not $hasDefaultProfile -or -not $hasWorkProfile) {
        throw "Default and Work browser tabs were not exposed with profile-aware names."
    }
    $webViews = @($allNodes | Where-Object {
        $_.Current.Name -like "Interactive web page, * profile" -and
        $_.Current.AutomationId -like "WebView-*"
    })
    if ($webViews.Count -ne 1) {
        throw "The selected tab was not exposed as exactly one profile-named interactive WebView."
    }
    $defaultProfilePath = Join-Path $dataRoot "WebView2\Profiles\Default"
    $workProfilePath = Join-Path $dataRoot "WebView2\Profiles\Work"
    if (-not (Test-Path $defaultProfilePath) -or -not (Test-Path $workProfilePath)) {
        throw "Default and Work WebView2 data directories were not physically isolated."
    }

    "HTTPS fixture=https://example.com/"
    "Accessible tab count=$secondTab"
    "Accessible profile-named tab selectors=$($accessibleTabs.Count)"
    "Selected accessible WebView count=$($webViews.Count)"
    "Profile control=$profileName"
    "Isolated profiles=Default, Work"
    "Agent drawer=$($agentDrawer.Current.Name)"
    "Keyboard shortcuts=Control L, Control T, Control W"
    "Direct live-page field=Approval code"
    "Accessible bookmarks=$bookmarkCount"
    "Accessible history entries=$historyCount"
    "Accessible downloads=$downloadCount"
    "Isolated download=$downloadPath"
    "P01 automated UIA validation=PASS"
}
finally {
    if ($null -ne $process -and -not $process.HasExited) {
        $null = $process.CloseMainWindow()
        if (-not $process.WaitForExit(5000)) {
            $process.Kill()
        }
    }
    if ($null -ne $fixtureProcess -and -not $fixtureProcess.HasExited) {
        Stop-Process -Id $fixtureProcess.Id -Force
    }
    $env:DCX_DATA_ROOT = $previousDataRoot
    $env:DCX_DOWNLOAD_ROOT = $previousDownloadRoot
}
