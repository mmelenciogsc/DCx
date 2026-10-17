[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
$tests = Join-Path $repoRoot "tests\DCx.Core.Tests\DCx.Core.Tests.csproj"
$exe = Join-Path $repoRoot "src\DCx.Desktop\bin\Release\net10.0-windows10.0.19041.0\DCx.exe"
$runRoot = Join-Path $repoRoot "artifacts\test-results\p12-package-approval-$([Guid]::NewGuid().ToString('N'))"
$packagePath = Join-Path $runRoot "synthetic-package-approval-fixture.msix"
$trxName = "p12-package-approval.trx"
$previousFixture = $env:DCX_PACKAGE_APPROVAL_FIXTURE
$previousFixtureRoot = $env:DCX_PACKAGE_APPROVAL_FIXTURE_ROOT
$process = $null

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

function Wait-For([scriptblock]$condition, [string]$failureMessage, [int]$attempts = 60) {
    for ($attempt = 0; $attempt -lt $attempts; $attempt++) {
        $result = & $condition
        if ($result) { return $result }
        Start-Sleep -Milliseconds 250
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

function Start-ReviewProcess {
    $start = [System.Diagnostics.ProcessStartInfo]::new($exe)
    $start.UseShellExecute = $false
    $null = $start.ArgumentList.Add("--package-approval-fixture")
    $null = $start.ArgumentList.Add($packagePath)
    $started = [System.Diagnostics.Process]::Start($start)
    if ($null -eq $started) { throw "DCx package review fixture did not start." }
    return $started
}

function Get-ChildProcesses([int]$parentProcessId) {
    return @(Get-CimInstance Win32_Process -Filter "ParentProcessId = $parentProcessId")
}

function Stop-ReviewProcess {
    if ($null -eq $script:process -or $script:process.HasExited) { return }
    $null = $script:process.CloseMainWindow()
    if (-not $script:process.WaitForExit(5000)) { $script:process.Kill() }
}

function Assert-RequestDetails($window) {
    $disclosure = Wait-For {
        Find-ById $window "PackageInstallValidationDisclosure"
    } "The non-executing validation disclosure was not exposed."
    if ($disclosure.Current.Name -notmatch "No package installation, elevation, PowerShell, or installer process") {
        throw "The validation disclosure did not expose the no-execution boundary."
    }

    $details = Wait-For {
        Find-ById $window "PackageInstallRequestDetails"
    } "The exact package request details were not exposed."
    $value = $details.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).Current.Value
    foreach ($expected in @(
        "Requested operation: msix-current-user-install",
        "Windows.Management.Deployment.PackageManager.AddPackageByUriAsync",
        "Deployment options: None",
        "Installation scope: Current Windows user",
        "Requests elevation: no",
        "DCx.AgenticBrowser.ValidationFixture",
        "CN=DCx Synthetic Validation Fixture",
        "Package SHA-256:",
        "Proposal SHA-256:",
        "Review expires UTC:")) {
        if ($value -notlike "*$expected*") {
            throw "The exact request preview omitted: $expected"
        }
    }
    if ($value -match "(?i)powershell\.exe|add-appxpackage|msiexec\.exe") {
        throw "The typed deployment preview unexpectedly exposed a shell command."
    }
}

New-Item -ItemType Directory -Path $runRoot -Force | Out-Null
[IO.File]::WriteAllBytes(
    $packagePath,
    [Text.Encoding]::UTF8.GetBytes("Synthetic MSIX approval fixture. It is not installable and must never execute."))
$env:DCX_PACKAGE_APPROVAL_FIXTURE = "1"
$env:DCX_PACKAGE_APPROVAL_FIXTURE_ROOT = $runRoot

Push-Location $repoRoot
try {
    dotnet test $tests --configuration Release --no-build --no-restore `
        --filter "FullyQualifiedName~PackageInstallReviewTests" `
        --logger "trx;LogFileName=$trxName" `
        --results-directory $runRoot
    if ($LASTEXITCODE -ne 0) { throw "Package installation review policy tests failed." }

    [xml]$trx = Get-Content -LiteralPath (Join-Path $runRoot $trxName) -Raw
    $counters = $trx.TestRun.ResultSummary.Counters
    if ([int]$counters.total -ne 12 -or [int]$counters.passed -ne 12 -or
        [int]$counters.failed -ne 0 -or [int]$counters.notExecuted -ne 0) {
        throw "Package review test counters were not exactly 12 passed and zero failed or skipped."
    }

    $process = Start-ReviewProcess
    $window = Wait-For {
        Find-WindowByProcessAndId $process.Id "PackageInstallReviewWindow"
    } "The package installation review window did not appear."
    Assert-RequestDetails $window

    $approval = Wait-For {
        Find-ById $window "PackageInstallApproval"
    } "The explicit package approval checkbox was not exposed."
    $approvalToggle = $approval.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern)
    if ($approvalToggle.Current.ToggleState -ne [System.Windows.Automation.ToggleState]::Off) {
        throw "Package installation approval did not default to unchecked."
    }

    $approve = Find-ById $window "PackageInstallApproveButton"
    $approve.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    $status = Find-ById $window "PackageInstallStatus"
    $null = Wait-For {
        $status.Current.Name -eq "Approval was not recorded. Check the exact-request approval box first. No installer was started."
    } "Unchecked approval did not fail closed with an assertive status."
    if (@(Get-ChildProcesses $process.Id).Count -ne 0) {
        throw "DCx launched a child process after denied package approval."
    }

    $approvalToggle.Toggle()
    $approve.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    $null = Wait-For {
        $status.Current.Name -eq "Approval recorded for this exact package. Validation mode did not permit execution or start an installer."
    } "Exact package approval did not produce the inert validation receipt."
    if ($approval.Current.IsEnabled -or $approve.Current.IsEnabled) {
        throw "The completed package request remained reusable."
    }
    if (@(Get-ChildProcesses $process.Id).Count -ne 0) {
        throw "DCx launched a child process after recording package approval."
    }
    (Find-ById $window "PackageInstallCloseButton").GetCurrentPattern(
        [System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    if (-not $process.WaitForExit(5000)) { throw "The approved review window did not close." }
    $process = $null

    $process = Start-ReviewProcess
    $window = Wait-For {
        Find-WindowByProcessAndId $process.Id "PackageInstallReviewWindow"
    } "The package cancellation review window did not appear."
    (Find-ById $window "PackageInstallCancelButton").GetCurrentPattern(
        [System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    $status = Find-ById $window "PackageInstallStatus"
    $null = Wait-For {
        $status.Current.Name -eq "Installation request cancelled. No installer was started."
    } "Package cancellation did not return an assertive no-execution status."
    if (@(Get-ChildProcesses $process.Id).Count -ne 0) {
        throw "DCx launched a child process after package cancellation."
    }
    (Find-ById $window "PackageInstallCloseButton").GetCurrentPattern(
        [System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    if (-not $process.WaitForExit(5000)) { throw "The cancelled review window did not close." }
    $process = $null

    "P12 package review policy tests passed=12"
    "P12 exact typed package request preview=PASS"
    "P12 approval default unchecked and denial=PASS"
    "P12 digest and trust revalidation approval receipt=PASS"
    "P12 package cancellation=PASS"
    "P12 installer child processes started=0"
    "P12 package approval UIA validation=PASS"
    "P12 real package installation=BLOCKED"
    "P12 package approval evidence=$runRoot"
}
finally {
    Stop-ReviewProcess
    $env:DCX_PACKAGE_APPROVAL_FIXTURE = $previousFixture
    $env:DCX_PACKAGE_APPROVAL_FIXTURE_ROOT = $previousFixtureRoot
    Pop-Location
}
