[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
$solution = Join-Path $repoRoot "DCx.slnx"
$tests = Join-Path $repoRoot "tests\DCx.Core.Tests\DCx.Core.Tests.csproj"
$exe = Join-Path $repoRoot "src\DCx.Desktop\bin\Release\net10.0-windows10.0.19041.0\DCx.exe"
$runRoot = Join-Path $repoRoot "artifacts\test-results\p10-$([Guid]::NewGuid().ToString('N'))"
$previousDataRoot = $env:DCX_DATA_ROOT
$contributionTrx = Join-Path $runRoot "p10-contributions.trx"
$globalService = Join-Path $repoRoot "server\DCx.GlobalService\bin\Release\net10.0\DCx.GlobalService.dll"
$serverProcess = $null
$serverOutputTask = $null
$serverErrorTask = $null
$contributorToken = $null
$reviewerToken = $null

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

function Convert-ToWslPath([string]$path) {
    $canonical = [IO.Path]::GetFullPath($path)
    if ($canonical -notmatch '^([A-Za-z]):\\(.*)$') {
        throw "The native path cannot be mapped to the local WSL mount: $canonical"
    }

    $drive = $Matches[1].ToLowerInvariant()
    $relative = $Matches[2].Replace('\', '/')
    return "/mnt/$drive/$relative"
}

function Get-EphemeralPort {
    $listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
    try {
        $listener.Start()
        return ([Net.IPEndPoint]$listener.LocalEndpoint).Port
    }
    finally { $listener.Stop() }
}

function Get-TextSha256([string]$value) {
    $sha = [Security.Cryptography.SHA256]::Create()
    try { return [Convert]::ToHexString($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($value))).ToLowerInvariant() }
    finally { $sha.Dispose() }
}

function Test-TreeContainsText([string]$root, [string]$value) {
    if (-not (Test-Path -LiteralPath $root -PathType Container)) { return $false }
    foreach ($file in Get-ChildItem -LiteralPath $root -File -Recurse) {
        try { $bytes = [IO.File]::ReadAllBytes($file.FullName) }
        catch [IO.FileNotFoundException] { continue }
        catch [IO.DirectoryNotFoundException] { continue }
        $contents = [Text.Encoding]::UTF8.GetString($bytes)
        if ($contents.Contains($value, [StringComparison]::Ordinal)) { return $true }
    }
    return $false
}

New-Item -ItemType Directory -Path $runRoot -Force | Out-Null
Push-Location $repoRoot
try {
    dotnet restore $solution --locked-mode
    if ($LASTEXITCODE -ne 0) { throw "Locked restore failed with exit code $LASTEXITCODE." }
    dotnet build $solution --configuration Release --no-restore
    if ($LASTEXITCODE -ne 0) { throw "Release build failed with exit code $LASTEXITCODE." }
    dotnet test $tests --configuration Release --no-build --no-restore --filter "FullyQualifiedName~Contributions" `
        --logger "trx;LogFileName=p10-contributions.trx" --results-directory $runRoot
    if ($LASTEXITCODE -ne 0) { throw "P10 contribution tests failed with exit code $LASTEXITCODE." }
    [xml]$contributionReport = Get-Content -LiteralPath $contributionTrx -Raw
    $contributionCounters = $contributionReport.TestRun.ResultSummary.Counters
    $contributionTestsPassed = [int]$contributionCounters.passed
    if ($contributionTestsPassed -lt 1 -or
        [int]$contributionCounters.total -ne $contributionTestsPassed -or
        [int]$contributionCounters.executed -ne $contributionTestsPassed -or
        [int]$contributionCounters.failed -ne 0 -or
        [int]$contributionCounters.error -ne 0 -or
        [int]$contributionCounters.timeout -ne 0 -or
        [int]$contributionCounters.aborted -ne 0) {
        throw "P10 TRX counters did not prove that every selected contribution test passed."
    }

    $wslRepoRoot = Convert-ToWslPath $repoRoot
    $wslRunRoot = Convert-ToWslPath $runRoot
    & wsl.exe -- bash "$wslRepoRoot/scripts/validate-p10-sandbox.sh" "$wslRunRoot"
    if ($LASTEXITCODE -ne 0) { throw "P10 hostile CCB sandbox validation failed with exit code $LASTEXITCODE." }
    $sandboxEvidencePath = Join-Path $runRoot "sandbox-evidence.json"
    $sandboxEvidence = Get-Content -LiteralPath $sandboxEvidencePath -Raw | ConvertFrom-Json
    if ($sandboxEvidence.policy_version -ne "dcx-ccb-bwrap-v1" -or
        $sandboxEvidence.workspace_digest_enforced -ne $true -or
        $sandboxEvidence.host_files_hidden -ne $true -or
        $sandboxEvidence.system_files_hidden -ne $true -or
        $sandboxEvidence.network_denied -ne $true -or
        $sandboxEvidence.root_write_denied -ne $true -or
        $sandboxEvidence.environment_cleared -ne $true -or
        $sandboxEvidence.non_root_uid -ne 65534 -or
        $sandboxEvidence.outside_workspace_denied -ne $true -or
        $sandboxEvidence.symlink_denied -ne $true -or
        $sandboxEvidence.timeout_enforced -ne $true -or
        $sandboxEvidence.output_limit_enforced -ne $true) {
        throw "P10 hostile CCB sandbox evidence failed closed-policy assertions."
    }

    $keyPath = Join-Path $runRoot "simulation-signing-key.pk8"
    $signingKey = [Security.Cryptography.ECDsa]::Create([Security.Cryptography.ECCurve+NamedCurves]::nistP256)
    try { [IO.File]::WriteAllBytes($keyPath, $signingKey.ExportPkcs8PrivateKey()) }
    finally { $signingKey.Dispose() }
    $contributorToken = "contributor-$([Guid]::NewGuid().ToString('N'))"
    $reviewerToken = "reviewer-$([Guid]::NewGuid().ToString('N'))"
    $contributorPseudonym = "contributor:uia-fixture"
    $endpoint = "http://127.0.0.1:$(Get-EphemeralPort)/"
    $serverStart = [Diagnostics.ProcessStartInfo]::new("dotnet")
    $serverStart.UseShellExecute = $false
    $serverStart.CreateNoWindow = $true
    $serverStart.RedirectStandardOutput = $true
    $serverStart.RedirectStandardError = $true
    $null = $serverStart.ArgumentList.Add($globalService)
    $serverStart.Environment["DCX_GLOBAL_SIMULATION"] = "1"
    $serverStart.Environment["DCX_GLOBAL_DATA_ROOT"] = Join-Path $runRoot "simulation-server"
    $serverStart.Environment["DCX_GLOBAL_SIGNING_KEY"] = $keyPath
    $serverStart.Environment["DCX_GLOBAL_SIMULATION_URL"] = $endpoint
    $serverStart.Environment["DCX_GLOBAL_CONTRIBUTION_API"] = "1"
    $serverStart.Environment["DCX_GLOBAL_CONTRIBUTOR_TOKEN_SHA256"] = Get-TextSha256 $contributorToken
    $serverStart.Environment["DCX_GLOBAL_CONTRIBUTOR_PSEUDONYM"] = $contributorPseudonym
    $serverStart.Environment["DCX_GLOBAL_REVIEWER_TOKEN_SHA256"] = Get-TextSha256 $reviewerToken
    $serverStart.Environment["DCX_GLOBAL_REVIEWER_ID"] = "reviewer:uia-fixture"
    $serverStart.Environment["Logging__LogLevel__Default"] = "Warning"
    $serverProcess = [Diagnostics.Process]::Start($serverStart)
    if ($null -eq $serverProcess) { throw "The loopback contribution simulation did not start." }
    $serverOutputTask = $serverProcess.StandardOutput.ReadToEndAsync()
    $serverErrorTask = $serverProcess.StandardError.ReadToEndAsync()
    $null = Wait-For {
        try {
            $health = Invoke-RestMethod -Method Get -Uri ([Uri]::new([Uri]$endpoint, "health")) `
                -TimeoutSec 2 -NoProxy -MaximumRedirection 0
            if ($health.contributionApi -eq $true) { return $true }
        }
        catch { }
        return $false
    } "The loopback contribution simulation did not become healthy."

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
        $submissionApproval = Find-ById $window "ContributionSubmissionApprovalBox"
        $submitSimulation = Find-ById $window "SubmitContributionSimulationButton"
        $cancelSubmission = Find-ById $window "CancelContributionSubmissionButton"
        $submissionToken = Find-ById $window "ContributionSimulationTokenBox"
        $status = Find-ById $window "ContributionStatusText"
        foreach ($checkbox in @($optIn, $itemApproval, $rights, $license, $sensitive, $submissionApproval)) {
            $toggle = $checkbox.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern)
            if ($toggle.Current.ToggleState -ne [System.Windows.Automation.ToggleState]::Off) {
                throw "A contribution consent or approval checkbox was not initially unchecked."
            }
        }
        if ($prepare.Current.IsEnabled) { throw "Preparation was enabled without consent and item approval." }
        if ($submitSimulation.Current.IsEnabled -or $cancelSubmission.Current.IsEnabled) {
            throw "A contribution send action was enabled without a prepared item and separate approval."
        }
        if ($status.Current.Name -notmatch "No network request occurs without a separate explicit loopback send approval") {
            throw "The default no-network state was not announced accessibly."
        }

        (Find-ById $window "ContributionResourceIdBox").GetCurrentPattern(
            [System.Windows.Automation.ValuePattern]::Pattern).SetValue("resource:uia-submission")
        (Find-ById $window "ContributionTitleBox").GetCurrentPattern(
            [System.Windows.Automation.ValuePattern]::Pattern).SetValue("Accessible UIA contribution")
        (Find-ById $window "ContributionBodyBox").GetCurrentPattern(
            [System.Windows.Automation.ValuePattern]::Pattern).SetValue("A reviewed deterministic local simulation contribution.")
        (Find-ById $window "ContributionPseudonymBox").GetCurrentPattern(
            [System.Windows.Automation.ValuePattern]::Pattern).SetValue($contributorPseudonym)
        (Find-ById $window "ContributionSimulationEndpointBox").GetCurrentPattern(
            [System.Windows.Automation.ValuePattern]::Pattern).SetValue($endpoint)
        foreach ($checkbox in @($optIn, $rights, $license, $sensitive)) {
            $checkbox.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Toggle()
        }
        (Find-ById $window "ReviewContributionButton").GetCurrentPattern(
            [System.Windows.Automation.InvokePattern]::Pattern).Invoke()
        $null = Wait-For { $status.Current.Name -like "Automated scan found no blocking issue.*" } `
            "The UIA fixture did not pass local review."
        $itemApproval.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Toggle()
        $null = Wait-For { $prepare.Current.IsEnabled } "The fully approved local preparation action was not enabled."
        $prepare.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
        $null = Wait-For {
            $status.Current.Name -like "Prepared locally as Quarantined.*separate approval.*"
        } "The exact reviewed item was not prepared locally."
        if (-not $submissionApproval.Current.IsEnabled -or $submitSimulation.Current.IsEnabled) {
            throw "Loopback send approval was not separate and default-off after preparation."
        }
        $submissionTokenValue = $submissionToken.GetCurrentPattern(
            [System.Windows.Automation.ValuePattern]::Pattern)
        $submissionTokenValue.SetValue($contributorToken)
        $submissionApproval.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Toggle()
        $null = Wait-For { $submitSimulation.Current.IsEnabled } "The separately approved loopback send did not enable."
        $submitSimulation.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
        $null = Wait-For {
            $status.Current.Name -like "The local simulation accepted submission:* into Quarantined.*"
        } "The native client did not submit the exact prepared item to loopback quarantine."
        if ($submissionApproval.GetCurrentPattern(
                [System.Windows.Automation.TogglePattern]::Pattern).Current.ToggleState -ne
            [System.Windows.Automation.ToggleState]::Off) {
            throw "The one-time loopback submission approval did not reset."
        }
        if (-not [string]::IsNullOrEmpty($submissionTokenValue.Current.Value)) {
            throw "The one-time loopback contributor token was not cleared from the password control."
        }

        "Contribution global opt-in initially unchecked=True"
        "Per-item approval initially unchecked=True"
        "Rights license and sensitive-data attestations initially unchecked=True"
        "Prepare action disabled without all gates=True"
        "Separate loopback send approval initially unchecked=True"
        "No-network-without-send-approval status exposed through UIA=True"
        "Native prepared-item loopback quarantine submission=PASS"
        "One-time contributor token not persisted and approval reset=PASS"
        "Hostile CCB namespace sandbox=PASS"
        "CCB host/system filesystem and network denial=PASS"
        "CCB non-root, clean-environment and resource limits=PASS"
        "CCB sandbox evidence=$sandboxEvidencePath"
        "P10 contribution tests=PASS ($contributionTestsPassed/$contributionTestsPassed)"
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
    $tokenLeakDetected = $false
    if ($null -ne $serverProcess -and -not $serverProcess.HasExited) {
        $serverProcess.Kill($true)
        $serverProcess.WaitForExit()
    }
    if ($null -ne $serverOutputTask -and $null -ne $serverErrorTask) {
        $serverDiagnostics = $serverOutputTask.GetAwaiter().GetResult() + $serverErrorTask.GetAwaiter().GetResult()
        if (($null -ne $contributorToken -and $serverDiagnostics -like "*$contributorToken*") -or
            ($null -ne $reviewerToken -and $serverDiagnostics -like "*$reviewerToken*")) {
            $tokenLeakDetected = $true
        }
    }
    if (($null -ne $contributorToken -and (Test-TreeContainsText $runRoot $contributorToken)) -or
        ($null -ne $reviewerToken -and (Test-TreeContainsText $runRoot $reviewerToken))) {
        $tokenLeakDetected = $true
    }
    $env:DCX_DATA_ROOT = $previousDataRoot
    Pop-Location
    if ($tokenLeakDetected) { throw "The loopback contribution simulation diagnostics exposed a bearer token." }
}
