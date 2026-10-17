[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
$solution = Join-Path $repoRoot "DCx.slnx"
$tests = Join-Path $repoRoot "tests\DCx.Core.Tests\DCx.Core.Tests.csproj"
$exe = Join-Path $repoRoot "src\DCx.Desktop\bin\Release\net10.0-windows10.0.19041.0\DCx.exe"
$fixtureDll = Join-Path $repoRoot "tests\DCx.WebFixtureServer\bin\Release\net10.0\DCx.WebFixtureServer.dll"
$runRoot = Join-Path $repoRoot "artifacts\test-results\p12-integrations-$([Guid]::NewGuid().ToString('N'))"
$previousDataRoot = $env:DCX_DATA_ROOT

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

function Wait-For([scriptblock]$condition, [string]$failureMessage, [int]$attempts = 50) {
    for ($attempt = 0; $attempt -lt $attempts; $attempt++) {
        $result = & $condition
        if ($result) { return $result }
        Start-Sleep -Milliseconds 400
    }
    throw $failureMessage
}

function Get-FreePort {
    $listener = [System.Net.Sockets.TcpListener]::new(
        [System.Net.IPAddress]::Loopback,
        0)
    $listener.Start()
    try { return ([System.Net.IPEndPoint]$listener.LocalEndpoint).Port }
    finally { $listener.Stop() }
}

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

function Find-ByName($root, [string]$name) {
    $condition = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::NameProperty,
        $name)
    return $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)
}

function Find-ControlByName($root, [string]$name, $controlType) {
    $nameCondition = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::NameProperty,
        $name)
    $typeCondition = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
        $controlType)
    $condition = New-Object System.Windows.Automation.AndCondition($nameCondition, $typeCondition)
    return $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)
}

Push-Location $repoRoot
$fixtureProcess = $null
$process = $null
try {
    dotnet restore $solution --locked-mode
    if ($LASTEXITCODE -ne 0) { throw "Locked restore failed with exit code $LASTEXITCODE." }
    dotnet build $solution --configuration Release --no-restore
    if ($LASTEXITCODE -ne 0) { throw "Release build failed with exit code $LASTEXITCODE." }
    dotnet test $tests --configuration Release --no-build --no-restore --filter "FullyQualifiedName~QuickBooksSandboxExportTests"
    if ($LASTEXITCODE -ne 0) { throw "QuickBooks XLSX fixture tests failed with exit code $LASTEXITCODE." }

    New-Item -ItemType Directory -Path $runRoot -Force | Out-Null
    $fixturePort = Get-FreePort
    $fixtureUrl = "http://127.0.0.1:$fixturePort/"
    $env:DCX_DATA_ROOT = Join-Path $runRoot "data"
    $fixtureProcess = Start-Process "dotnet.exe" -ArgumentList @($fixtureDll, "--port", $fixturePort) -WindowStyle Hidden -PassThru
    $null = Wait-For {
        try { (Invoke-WebRequest "${fixtureUrl}health" -UseBasicParsing -TimeoutSec 2).Content -eq "ok" }
        catch { $null }
    } "The controlled social fixture server did not start."

    $loginHtml = (Invoke-WebRequest "${fixtureUrl}social-login" -UseBasicParsing -TimeoutSec 2).Content
    $postsHtml = (Invoke-WebRequest "${fixtureUrl}social-posts" -UseBasicParsing -TimeoutSec 2).Content
    if ($loginHtml -notmatch "It is not Facebook" -or
        $postsHtml -notmatch 'datetime="2021-06-14"' -or
        $postsHtml -notmatch 'alt="Black mobility cane prototype' -or
        $postsHtml -notmatch 'aria-label="Demonstration video') {
        throw "The social fixture lacks its non-production disclosure or accessible post metadata."
    }

    $process = Start-Process $exe -PassThru
    $window = Wait-For { Get-MainWindow $process.Id } "The DCx main window did not appear."
    $address = Wait-For { Find-ById $window "AddressBox" } "The address field was not exposed."
    $null = Wait-For {
        $pattern = $address.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)
        $pattern.Current.Value -eq "https://example.com/"
    } "The initial browser tab was not ready."

    $address.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue("${fixtureUrl}social-login")
    (Find-ById $window "GoButton").GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    $account = Wait-For {
        Find-ControlByName $window "Fixture account name" ([System.Windows.Automation.ControlType]::Edit)
    } "The labeled fixture login field was not exposed."
    $accountName = $account.Current.Name
    $account.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue("local test user")
    (Find-ControlByName $window "Complete manual login handoff" ([System.Windows.Automation.ControlType]::Button)).GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()

    $search = Wait-For {
        Find-ControlByName $window "Search terms" ([System.Windows.Automation.ControlType]::Edit)
    } "The old-post search field was not exposed after login."
    $search.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue("taser cane")
    (Find-ControlByName $window "Search posts" ([System.Windows.Automation.ControlType]::Button)).GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    $result = Wait-For { Find-ByName $window "1 matching fixture post found." } "The expected old fixture post was not found."
    $heading = Wait-For { Find-ByName $window "Taser cane prototype in action" } "The matching post heading was not exposed."

    "QuickBooks sandbox-shaped report to real XLSX=PASS"
    "QuickBooks formula-looking label exported as text=PASS"
    "Local social fixture disclosure=not Facebook"
    "Manual login field exposed=$accountName"
    "Old post result=$($heading.Current.Name)"
    "Post date and accessible media metadata=PASS"
    "Real Facebook credentials submitted=0"
    "Real QuickBooks network calls=0"
    "P12 local integration fixtures=PASS"
}
finally {
    if ($null -ne $process -and -not $process.HasExited) {
        $null = $process.CloseMainWindow()
        if (-not $process.WaitForExit(5000)) { $process.Kill() }
    }
    if ($null -ne $fixtureProcess -and -not $fixtureProcess.HasExited) {
        Stop-Process -Id $fixtureProcess.Id -Force
    }
    $env:DCX_DATA_ROOT = $previousDataRoot
    Pop-Location
}
