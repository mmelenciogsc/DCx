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
$previousFacebookFixtureGate = $env:DCX_P12_FACEBOOK_FIXTURE
$previousFacebookFixtureOrigin = $env:DCX_P12_FACEBOOK_FIXTURE_ORIGIN

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

function Get-TopLevelWindowByName([int]$processId, [string]$name) {
    $processCondition = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ProcessIdProperty,
        $processId)
    $nameCondition = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::NameProperty,
        $name)
    $typeCondition = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
        [System.Windows.Automation.ControlType]::Window)
    $condition = New-Object System.Windows.Automation.AndCondition($processCondition, $nameCondition, $typeCondition)
    return [System.Windows.Automation.AutomationElement]::RootElement.FindFirst(
        [System.Windows.Automation.TreeScope]::Descendants,
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
    New-Item -ItemType Directory -Path $runRoot -Force | Out-Null
    dotnet restore $solution --locked-mode
    if ($LASTEXITCODE -ne 0) { throw "Locked restore failed with exit code $LASTEXITCODE." }
    dotnet build $solution --configuration Release --no-restore
    if ($LASTEXITCODE -ne 0) { throw "Release build failed with exit code $LASTEXITCODE." }
    dotnet test $tests --configuration Release --no-build --no-restore `
        --filter "FullyQualifiedName~QuickBooksSandboxExportTests|FullyQualifiedName~FacebookHistoricalPostSearchTests" `
        --logger "trx;LogFileName=p12-integrations.trx" --results-directory $runRoot
    if ($LASTEXITCODE -ne 0) { throw "QuickBooks XLSX or historical-post policy tests failed with exit code $LASTEXITCODE." }
    [xml]$integrationReport = Get-Content -LiteralPath (Join-Path $runRoot "p12-integrations.trx") -Raw
    $integrationCounters = $integrationReport.TestRun.ResultSummary.Counters
    if ([int]$integrationCounters.passed -ne 17 -or
        [int]$integrationCounters.total -ne 17 -or
        [int]$integrationCounters.executed -ne 17 -or
        [int]$integrationCounters.failed -ne 0 -or
        [int]$integrationCounters.error -ne 0 -or
        [int]$integrationCounters.timeout -ne 0 -or
        [int]$integrationCounters.aborted -ne 0) {
        throw "P12 integration TRX counters did not prove the exact 17 selected tests passed."
    }

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

    $env:DCX_P12_FACEBOOK_FIXTURE = "1"
    $env:DCX_P12_FACEBOOK_FIXTURE_ORIGIN = $fixtureUrl
    $process = Start-Process $exe -PassThru
    $env:DCX_P12_FACEBOOK_FIXTURE = $previousFacebookFixtureGate
    $env:DCX_P12_FACEBOOK_FIXTURE_ORIGIN = $previousFacebookFixtureOrigin
    $window = Wait-For { Get-MainWindow $process.Id } "The DCx main window did not appear."
    $address = Wait-For { Find-ById $window "AddressBox" } "The address field was not exposed."
    $null = Wait-For {
        $pattern = $address.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)
        $pattern.Current.Value -eq "https://example.com/"
    } "The initial browser tab was not ready."

    (Find-ById $window "FacebookPostFinderButton").GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    $finder = $null
    for ($attempt = 0; $attempt -lt 50 -and $null -eq $finder; $attempt++) {
        $finder = Get-TopLevelWindowByName $process.Id "Find an old Facebook post"
        if ($null -eq $finder) { Start-Sleep -Milliseconds 400 }
    }
    if ($null -eq $finder) {
        $mainStatus = Find-ById $window "StatusText"
        $statusName = if ($null -eq $mainStatus) { "unavailable" } else { $mainStatus.Current.Name }
        $processCondition = New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::ProcessIdProperty,
            $process.Id)
        $processWindows = [System.Windows.Automation.AutomationElement]::RootElement.FindAll(
            [System.Windows.Automation.TreeScope]::Children,
            $processCondition)
        $windowNames = @($processWindows | ForEach-Object { "name='$($_.Current.Name)' id='$($_.Current.AutomationId)'" }) -join "; "
        throw "The native historical-post finder did not open. Main status: $statusName. Process windows: $windowNames"
    }
    $heading = Find-ById $finder "FacebookHistoricalPostSearchHeading"
    if ($null -eq $heading -or
        $heading.Current.Name -ne "Find an old Facebook post" -or
        -not $heading.Current.HasKeyboardFocus) {
        throw "The historical-post finder heading was not exposed with initial keyboard focus."
    }
    $boundary = Find-ById $finder "FacebookHistoricalPostSearchBoundary"
    if ($null -eq $boundary -or
        $boundary.Current.Name -notlike "Controlled local validation fixture*not Facebook*accepts no real credential*no-fabricated-result boundaries.*") {
        throw "The historical-post finder did not expose its exact local non-Facebook boundary."
    }

    (Find-ById $finder "FacebookOpenManualLoginButton").GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    $account = Wait-For {
        Find-ControlByName $window "Fixture account name" ([System.Windows.Automation.ControlType]::Edit)
    } "The labeled fixture login field was not exposed."
    $accountName = $account.Current.Name
    $account.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue("local test user")
    (Find-ControlByName $window "Complete manual login handoff" ([System.Windows.Automation.ControlType]::Button)).GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()

    $loginComplete = Find-ById $finder "FacebookManualLoginCompleteCheckBox"
    $loginComplete.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Toggle()
    $handBack = Find-ById $finder "FacebookHandBackAfterLoginButton"
    $null = Wait-For { if ($handBack.Current.IsEnabled) { $handBack } else { $null } } "Manual-login handback did not become available."
    $handBack.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    $query = Find-ById $finder "FacebookHistoricalPostSearchQuery"
    $query.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue("taser cane")
    (Find-ById $finder "FacebookOpenPostSearchButton").GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()

    $result = Wait-For { Find-ByName $window "1 matching fixture post found." } "The expected old fixture post was not found."
    $postHeading = Wait-For { Find-ByName $window "Taser cane prototype in action" } "The matching post heading was not exposed."
    $postHeadingName = $postHeading.Current.Name
    (Find-ControlByName $window "Open this fixture post" ([System.Windows.Automation.ControlType]::Hyperlink)).GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    $null = Wait-For {
        $value = $address.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).Current.Value
        if ($value -eq "${fixtureUrl}social-posts#taser-cane-2021") { $value } else { $null }
    } "The fixture candidate did not become the current direct post address."

    (Find-ById $finder "FacebookInspectCurrentPostButton").GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    $summary = Wait-For {
        $candidate = Find-ById $finder "FacebookPostInspectionSummary"
        if ($null -ne $candidate -and $candidate.Current.Name -like "One current direct post was inspected*") { $candidate } else { $null }
    } "The current direct fixture post was not verified."
    if (-not $summary.Current.HasKeyboardFocus) {
        throw "The historical-post result summary did not receive keyboard focus."
    }
    $directAddress = Find-ById $finder "FacebookInspectedDirectAddress"
    $verifiedAddress = $directAddress.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).Current.Value
    if ($verifiedAddress -ne "${fixtureUrl}social-posts#taser-cane-2021") {
        throw "The historical-post finder did not return the exact inspected current address."
    }
    $details = Find-ById $finder "FacebookInspectedPostDetails"
    if ($details.Current.Name -notlike "Heading: Taser cane prototype in action.*Published: June 14, 2021.*") {
        throw "The historical-post finder did not expose bounded accessible post evidence."
    }

    $address.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue("${fixtureUrl}social-blocked")
    (Find-ById $window "GoButton").GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    $null = Wait-For { Find-ByName $window "Assisted access blocked" } "The controlled blocked-site fixture did not load."
    (Find-ById $finder "FacebookInspectCurrentPostButton").GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    $blockedSummary = Wait-For {
        $candidate = Find-ById $finder "FacebookPostInspectionSummary"
        if ($null -ne $candidate -and $candidate.Current.Name -like "Facebook blocked, challenged, or changed*") { $candidate } else { $null }
    } "The blocked-site fallback was not exposed."
    $clearedAddress = $directAddress.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).Current.Value
    if ($clearedAddress.Length -ne 0 -or -not $blockedSummary.Current.HasKeyboardFocus) {
        throw "The blocked-site fallback retained a fabricated address or failed to focus its result."
    }

    $accessKeys = @(
        (Find-ById $finder "FacebookOpenManualLoginButton").Current.AccessKey,
        (Find-ById $finder "FacebookManualLoginCompleteCheckBox").Current.AccessKey,
        (Find-ById $finder "FacebookHandBackAfterLoginButton").Current.AccessKey,
        (Find-ById $finder "FacebookOpenPostSearchButton").Current.AccessKey,
        (Find-ById $finder "FacebookInspectCurrentPostButton").Current.AccessKey,
        (Find-ById $finder "FacebookReportBlockedButton").Current.AccessKey,
        (Find-ById $finder "FacebookCopyInspectedAddressButton").Current.AccessKey,
        (Find-ById $finder "FacebookReturnToBrowserButton").Current.AccessKey,
        (Find-ById $finder "FacebookClosePostSearchButton").Current.AccessKey
    )
    $uniqueAccessKeys = @($accessKeys | ForEach-Object { $_.ToUpperInvariant() } | Sort-Object -Unique)
    if ($accessKeys.Where({ [string]::IsNullOrWhiteSpace($_) }).Count -ne 0 -or
        $uniqueAccessKeys.Count -ne $accessKeys.Count) {
        throw "The historical-post workflow lacks distinct keyboard access keys."
    }

    $evidence = [ordered]@{
        captured_at_utc = (Get-Date).ToUniversalTime().ToString("O")
        surface = "native shared WebView2 historical-post finder"
        fixture_disclosure = "not Facebook; accepts no real credential"
        policy_tests_passed = 15
        selected_integration_tests_passed = 17
        manual_login_handoff = $true
        inspected_current_address = $verifiedAddress
        blocked_site_result_address = $clearedAddress
        blocked_site_no_fabrication = $true
        keyboard_access_keys = $accessKeys.Count
        real_facebook_requests = 0
        credentials_read = 0
        cookies_copied = 0
        production_actions = 0
    }
    $evidence | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $runRoot "facebook-post-search-uia.json") -Encoding utf8

    "QuickBooks sandbox-shaped report to real XLSX=PASS"
    "QuickBooks formula-looking label exported as text=PASS"
    "Local social fixture disclosure=not Facebook"
    "Manual login field exposed=$accountName"
    "Old post result=$postHeadingName"
    "Inspected direct address=$verifiedAddress"
    "Blocked-site no-fabrication fallback=PASS"
    "Historical-post keyboard access keys=$($accessKeys.Count)"
    "Post date and accessible media metadata=PASS"
    "Real Facebook credentials submitted=0"
    "Real Facebook network requests=0"
    "Real QuickBooks network calls=0"
    "P12 local integration fixtures=PASS"
    "P12 integration evidence=$runRoot"
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
    $env:DCX_P12_FACEBOOK_FIXTURE = $previousFacebookFixtureGate
    $env:DCX_P12_FACEBOOK_FIXTURE_ORIGIN = $previousFacebookFixtureOrigin
    Pop-Location
}
