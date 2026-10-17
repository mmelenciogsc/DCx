[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
$solution = Join-Path $repoRoot "DCx.slnx"
$tests = Join-Path $repoRoot "tests\DCx.Core.Tests\DCx.Core.Tests.csproj"
$exe = Join-Path $repoRoot "src\DCx.Desktop\bin\Release\net10.0-windows10.0.19041.0\DCx.exe"
$runRoot = Join-Path $repoRoot "artifacts\test-results\p10-$([Guid]::NewGuid().ToString('N'))"
$previousDataRoot = $env:DCX_DATA_ROOT

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

function Find-ById($root, [string]$automationId) {
    $condition = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::AutomationIdProperty,
        $automationId)
    return $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)
}

function Find-Window([int]$processId, [string]$automationId) {
    $elements = [System.Windows.Automation.AutomationElement]::RootElement.FindAll(
        [System.Windows.Automation.TreeScope]::Descendants,
        [System.Windows.Automation.Condition]::TrueCondition)
    return @($elements | Where-Object {
        $_.Current.ProcessId -eq $processId -and $_.Current.AutomationId -eq $automationId
    }) | Select-Object -First 1
}

New-Item -ItemType Directory -Path $runRoot -Force | Out-Null
Push-Location $repoRoot
try {
    dotnet restore $solution --locked-mode
    if ($LASTEXITCODE -ne 0) { throw "Locked restore failed with exit code $LASTEXITCODE." }
    dotnet build $solution --configuration Release --no-restore
    if ($LASTEXITCODE -ne 0) { throw "Release build failed with exit code $LASTEXITCODE." }
    dotnet test $tests --configuration Release --no-build --no-restore --filter "FullyQualifiedName~Contributions"
    if ($LASTEXITCODE -ne 0) { throw "P10 contribution tests failed with exit code $LASTEXITCODE." }

    $env:DCX_DATA_ROOT = Join-Path $runRoot "data"
    $process = Start-Process $exe -PassThru
    try {
        $main = Wait-For { Find-Window $process.Id "" } "The DCx main window did not open."
        $button = Wait-For { Find-ById $main "ContributionsButton" } "The contribution review button was not exposed."
        $button.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
        $window = Wait-For { Find-Window $process.Id "ContributionsWindow" } "The contribution review window did not open."
        $optIn = Find-ById $window "ContributionOptInBox"
        $itemApproval = Find-ById $window "ContributionItemApprovalBox"
        $rights = Find-ById $window "ContributionRightsBox"
        $license = Find-ById $window "ContributionLicenseAttestationBox"
        $sensitive = Find-ById $window "ContributionSensitiveReviewBox"
        $prepare = Find-ById $window "PrepareContributionButton"
        $status = Find-ById $window "ContributionStatusText"
        foreach ($checkbox in @($optIn, $itemApproval, $rights, $license, $sensitive)) {
            $toggle = $checkbox.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern)
            if ($toggle.Current.ToggleState -ne [System.Windows.Automation.ToggleState]::Off) {
                throw "A contribution consent or approval checkbox was not initially unchecked."
            }
        }
        if ($prepare.Current.IsEnabled) { throw "Preparation was enabled without consent and item approval." }
        if ($status.Current.Name -notmatch "No network upload") { throw "The no-upload state was not announced accessibly." }

        "Contribution global opt-in initially unchecked=True"
        "Per-item approval initially unchecked=True"
        "Rights license and sensitive-data attestations initially unchecked=True"
        "Prepare action disabled without all gates=True"
        "No-upload status exposed through UIA=True"
        "P10 contribution governance validation=PASS"
    }
    finally {
        if ($null -ne $process -and -not $process.HasExited) {
            $null = $process.CloseMainWindow()
            if (-not $process.WaitForExit(5000)) { $process.Kill() }
        }
    }
}
finally {
    $env:DCX_DATA_ROOT = $previousDataRoot
    Pop-Location
}
