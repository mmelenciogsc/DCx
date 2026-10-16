[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
$exe = Join-Path $repoRoot "src\DCx.Desktop\bin\Release\net10.0-windows10.0.19041.0\DCx.exe"
$fakeServer = Join-Path $repoRoot "tests\DCx.FakeCodexAppServer\bin\Release\net10.0\DCx.FakeCodexAppServer.exe"
$previousRepoRoot = $env:DCX_REPO_ROOT
$previousDataRoot = $env:DCX_DATA_ROOT
$previousTestMode = $env:DCX_TEST_MODE
$previousTestServer = $env:DCX_CODEX_TEST_SERVER
$previousCredentialTarget = $env:DCX_TEST_CREDENTIAL_TARGET
$runId = [Guid]::NewGuid().ToString("N")
$runRoot = Join-Path $repoRoot "artifacts\test-results\p04-$runId"
$testCredentialTarget = "DCx/Test/P04-$runId"

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

if (-not (Test-Path $exe) -or -not (Test-Path $fakeServer)) {
    throw "Build DCx and the offline Codex fixture before running P04 validation."
}

$env:DCX_REPO_ROOT = $repoRoot
$env:DCX_DATA_ROOT = Join-Path $runRoot "data"
$env:DCX_TEST_MODE = "1"
$env:DCX_CODEX_TEST_SERVER = $fakeServer
$env:DCX_TEST_CREDENTIAL_TARGET = $testCredentialTarget
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
    } "The DCx shell did not finish startup."

    $openAiButton = Find-ById $mainWindow "OpenAiButton"
    $openAiButton.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    $providerWindow = Wait-For {
        Find-WindowByProcessAndId $process.Id "OpenAiControlWindow"
    } "The OpenAI and local Codex window did not open."

    $chatGptButton = Find-ById $providerWindow "ChatGptSignInButton"
    if ($chatGptButton.Current.IsEnabled) {
        throw "Commercial ChatGPT sign-in was enabled without verified eligibility."
    }
    $eligibility = Find-ById $providerWindow "ChatGptEligibilityText"
    if ($eligibility.Current.Name -notlike "*approval*not been verified*") {
        throw "The blocked ChatGPT eligibility reason was not exposed accessibly."
    }

    $runtime = Find-ById $providerWindow "CodexRuntimeText"
    if ($runtime.Current.Name -notlike "Offline Codex protocol fixture detected*") {
        throw "The isolated offline Codex fixture was not selected."
    }

    $connect = Find-ById $providerWindow "ConnectCodexButton"
    $connect.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    $status = Find-ById $providerWindow "CodexStatusText"
    $null = Wait-For { $status.Current.Name -like "Local Codex connection ready. 1 model*" } "The JSONL handshake/model list did not complete."

    $models = Find-ById $providerWindow "ModelsBox"
    $modelSelection = $models.GetCurrentPattern([System.Windows.Automation.SelectionPattern]::Pattern).Current.GetSelection()
    if ($modelSelection.Count -ne 1 -or $modelSelection[0].Current.Name -ne "Offline fixture model") {
        $selectedNames = @($modelSelection | ForEach-Object { $_.Current.Name }) -join ", "
        throw "The discovered model was not selected accessibly. Count=$($modelSelection.Count); names=$selectedNames"
    }

    $prompt = Find-ById $providerWindow "PromptBox"
    $prompt.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue("Prepared offline fixture prompt.")
    $run = Find-ById $providerWindow "RunTurnButton"
    $run.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    $response = Find-ById $providerWindow "CodexResponseText"
    $null = Wait-For {
        $value = $response.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)
        $value.Current.Value -eq "Fixture response completed."
    } "The streamed fixture turn did not complete."

    $disconnect = Find-ById $providerWindow "DisconnectCodexButton"
    $disconnect.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    $null = Wait-For { $status.Current.Name -eq "Local Codex app-server disconnected." } "The app-server did not disconnect cleanly."

    "ChatGPT commercial sign-in enabled=False"
    "Eligibility reason accessible=True"
    "Per-user isolated test runtime=True"
    "JSONL initialize/initialized=PASS"
    "Discovered models=1"
    "Streamed turn response=Fixture response completed."
    "Explicit disconnect=PASS"
    "P04 offline provider UIA validation=PASS"
}
finally {
    cmdkey.exe /delete:$testCredentialTarget 2>$null | Out-Null
    if (-not $process.HasExited) {
        $null = $process.CloseMainWindow()
        if (-not $process.WaitForExit(5000)) { $process.Kill() }
    }
    $env:DCX_REPO_ROOT = $previousRepoRoot
    $env:DCX_DATA_ROOT = $previousDataRoot
    $env:DCX_TEST_MODE = $previousTestMode
    $env:DCX_CODEX_TEST_SERVER = $previousTestServer
    $env:DCX_TEST_CREDENTIAL_TARGET = $previousCredentialTarget
}
