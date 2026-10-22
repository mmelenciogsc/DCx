[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
$composeFile = Join-Path $repoRoot "server\docker-compose.u03-fixture.yml"
$fixtureFile = Join-Path $repoRoot "server\u03-fixture\index.html"
$exe = Join-Path $repoRoot "src\DCx.Desktop\bin\Release\net10.0-windows10.0.19041.0\DCx.exe"
$runRoot = Join-Path $repoRoot "artifacts\test-results\u03-$([Guid]::NewGuid().ToString('N'))"
$responseFile = Join-Path $runRoot "fixture-response.html"
$evidenceFile = Join-Path $runRoot "u03-validation.txt"
$fixtureUrl = "http://127.0.0.1:43189/"
$projectName = "dcx-u03-fixture-validation"
$serviceName = "u03-fixture"
$imageName = "$projectName-$serviceName`:latest"

# Every invocation below is equivalent to:
# docker compose --profile manual -p dcx-u03-fixture-validation -f server/docker-compose.u03-fixture.yml <command>
# The fixture is opt-in and is never part of normal Compose startup.

$previousDataRoot = $env:DCX_DATA_ROOT
$previousDownloadRoot = $env:DCX_DOWNLOAD_ROOT
$fixtureProcessStarted = $false
$dcxProcess = $null
$validationFailure = $null
$cleanupFailure = $null
$evidence = New-Object System.Collections.Generic.List[string]

function Add-Evidence([string]$line) {
    $script:evidence.Add($line)
    Write-Output $line
}

function Wait-For([scriptblock]$condition, [string]$failureMessage, [int]$attempts = 40) {
    for ($attempt = 0; $attempt -lt $attempts; $attempt++) {
        $result = & $condition
        if ($null -ne $result -and $result -ne $false) { return $result }
        Start-Sleep -Milliseconds 500
    }

    throw $failureMessage
}

function Invoke-U03Compose([string[]]$arguments) {
    $output = & docker compose --profile manual -p dcx-u03-fixture-validation -f $script:composeFile @arguments
    if ($LASTEXITCODE -ne 0) {
        throw "docker compose failed: $($arguments -join ' ')"
    }

    return $output
}

function Get-Sha256Text([string]$text) {
    $sha256 = [System.Security.Cryptography.SHA256]::Create()
    try {
        $bytes = [System.Text.Encoding]::UTF8.GetBytes($text)
        return ([System.BitConverter]::ToString($sha256.ComputeHash($bytes))).Replace("-", "").ToLowerInvariant()
    }
    finally {
        $sha256.Dispose()
    }
}

function Get-U03ExistingResources {
    $resources = @()
    $label = "label=com.docker.compose.project=$script:projectName"
    $resources += @(docker ps --all --quiet --filter $label 2>$null | Where-Object { $_ })
    $resources += @(docker network ls --quiet --filter $label 2>$null | Where-Object { $_ })
    $resources += @(docker volume ls --quiet --filter $label 2>$null | Where-Object { $_ })

    & docker image inspect $script:imageName *> $null
    if ($LASTEXITCODE -eq 0) {
        $resources += $script:imageName
    }

    return @($resources | Select-Object -Unique)
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

function Stop-Dcx($process) {
    if ($null -eq $process) { return }

    try {
        if (-not $process.HasExited) {
            $null = $process.CloseMainWindow()
            if (-not $process.WaitForExit(5000)) {
                $process.Kill()
                $process.WaitForExit(5000)
            }
        }
    }
    catch {
        Write-Warning "Could not stop the U03 DCx process cleanly: $($_.Exception.Message)"
    }
}

if ($env:OS -ne "Windows_NT") {
    throw "U03 validation is a native Windows harness and must be run in a Windows interactive session."
}

foreach ($path in @($composeFile, $fixtureFile, $exe)) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Required U03 build or fixture artifact is missing: $path"
    }
}

if ($null -eq (Get-Command docker -ErrorAction SilentlyContinue)) {
    throw "Docker CLI is unavailable."
}

& docker info *> $null
if ($LASTEXITCODE -ne 0) {
    throw "Docker daemon is unavailable."
}

$null = Invoke-U03Compose @("version")
$renderedCompose = (Invoke-U03Compose @("config")) -join "`n"
if ([string]::IsNullOrWhiteSpace($renderedCompose)) {
    throw "The U03 fixture Compose configuration was empty."
}

$existingResources = @(Get-U03ExistingResources)
if ($existingResources.Count -ne 0) {
    throw "Refusing to run because matching U03 Compose resources already exist for $projectName: $($existingResources -join ', ')"
}

New-Item -ItemType Directory -Path $runRoot -Force | Out-Null
$env:DCX_DATA_ROOT = Join-Path $runRoot "data"
$env:DCX_DOWNLOAD_ROOT = Join-Path $runRoot "downloads"

try {
    Add-Evidence "compose-file=$composeFile"
    Add-Evidence "compose-config-sha256=$(Get-Sha256Text $renderedCompose)"
    Add-Evidence "fixture-source-sha256=$((Get-FileHash -Algorithm SHA256 -LiteralPath $fixtureFile).Hash.ToLowerInvariant())"

    # Set before up so a partially created project is also removed by the finally block.
    $fixtureProcessStarted = $true
    $null = Invoke-U03Compose @("up", "--build", "--detach", "--wait")

    $response = Wait-For {
        try {
            $result = Invoke-WebRequest -Uri $fixtureUrl -OutFile $responseFile -TimeoutSec 2 -UseBasicParsing
            if ($result.StatusCode -eq 200 -and (Test-Path -LiteralPath $responseFile -PathType Leaf)) {
                return $true
            }
        }
        catch {
            return $null
        }
    } "The U03 fixture did not become healthy at http://127.0.0.1:43189."

    $fixtureHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $fixtureFile).Hash.ToLowerInvariant()
    $responseHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $responseFile).Hash.ToLowerInvariant()
    if ($fixtureHash -ne $responseHash) {
        throw "The HTTP health response at http://127.0.0.1:43189 did not exactly match the baked U03 fixture."
    }

    $servedFixture = Get-Content -LiteralPath $responseFile -Raw
    foreach ($requiredMarkup in @('for="fixture-text"', 'for="fixture-password"', 'type="password"', 'fixture-ready', 'take-control-requested', 'hand-control-back')) {
        if (-not $servedFixture.Contains($requiredMarkup)) {
            throw "The served U03 fixture is missing required inert proof markup."
        }
    }

    $containerId = (Invoke-U03Compose @("ps", "--quiet", $serviceName) | Select-Object -First 1).Trim()
    if ([string]::IsNullOrWhiteSpace($containerId)) {
        throw "The U03 fixture container ID was unavailable."
    }

    $imageDigest = (& docker inspect --format '{{.Image}}' $containerId).Trim()
    if ($LASTEXITCODE -ne 0 -or $imageDigest -notlike "sha256:*") {
        throw "The U03 fixture image digest was unavailable."
    }

    $portBindingJson = (& docker inspect --format '{{json .HostConfig.PortBindings}}' $containerId).Trim()
    if ($LASTEXITCODE -ne 0) {
        throw "The U03 fixture port binding was unavailable."
    }

    $portBindingMap = $portBindingJson | ConvertFrom-Json
    $publishedBindings = @($portBindingMap.PSObject.Properties['8080/tcp'].Value)
    if ($publishedBindings.Count -ne 1 -or
        $publishedBindings[0].HostIp -ne "127.0.0.1" -or
        $publishedBindings[0].HostPort -ne "43189") {
        throw "The U03 fixture is not bound exactly to 127.0.0.1:43189."
    }

    Add-Evidence "fixture-response-sha256=$responseHash"
    Add-Evidence "image-digest=$imageDigest"
    Add-Evidence "published-port=127.0.0.1:43189"
    Add-Evidence "docker-port-binding=$portBindingJson"
    Add-Evidence "health-check=http://127.0.0.1:43189=PASS"

    Add-Type -AssemblyName UIAutomationClient
    Add-Type -AssemblyName UIAutomationTypes

    $dcxProcess = Start-Process -FilePath $exe -PassThru
    $window = Wait-For { Get-MainWindow $dcxProcess.Id } "The DCx main window did not appear."
    $address = Wait-For { Find-ById $window "AddressBox" } "The observable DCx address field was not exposed through UI Automation."
    $goButton = Wait-For { Find-ById $window "GoButton" } "The observable DCx Go button was not exposed through UI Automation."
    $status = Wait-For { Find-ById $window "StatusText" } "The observable DCx status text was not exposed through UI Automation."

    $address.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue($fixtureUrl)
    $goButton.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    $null = Wait-For {
        $status.Current.Name -like "Page loaded in * profile."
    } "DCx did not report that the U03 fixture page loaded."

    $agentDrawer = Wait-For { Find-ById $window "AgentDrawerButton" } "The observable DCx agent-controls button was not exposed through UI Automation."
    $agentDrawer.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    $agentTarget = Wait-For { Find-ById $window "AgentTargetBox" } "The observable agent URL field was not exposed through UI Automation."
    $agentMode = Wait-For { Find-ById $window "AgentControlModeText" } "The observable agent control mode was not exposed through UI Automation."

    if ($agentTarget.Current.Name -ne "Controlled agent demo URL" -or $agentMode.Current.Name -notlike "Control: User*") {
        throw "The observable DCx agent-control surface did not expose its expected safe initial state."
    }

    Add-Evidence "uia-address-control=PASS"
    Add-Evidence "uia-go-control=PASS"
    Add-Evidence "uia-status-after-navigation=$($status.Current.Name)"
    Add-Evidence "uia-agent-controls=PASS"
    Add-Evidence "BLOCKED: Manual user editing, takeover, and hand-back require an attended foreground Windows session and were not automated."
    Add-Evidence "BLOCKED: Keyboard-focus-path validation requires a human foreground observation and was not claimed by UI Automation."
    Add-Evidence "BLOCKED: Narrator, NVDA, and JAWS observations require a human assistive-technology validation session and were not claimed."
    Add-Evidence "U03 automated fixture validation=PASS (Docker lifecycle, HTTP health, and observable host UIA only)."
}
catch {
    $validationFailure = $_
    Add-Evidence "U03 automated fixture validation=FAIL: $($_.Exception.Message)"
}
finally {
    Stop-Dcx $dcxProcess

    if ($fixtureProcessStarted) {
        try {
            # Always remove only this per-run project, its locally built image, and its volumes.
            $null = Invoke-U03Compose @("down", "--remove-orphans", "--rmi", "local", "--volumes")
            $remainingResources = @(Get-U03ExistingResources)
            if ($remainingResources.Count -ne 0) {
                throw "U03 cleanup left matching Compose resources: $($remainingResources -join ', ')"
            }

            Add-Evidence "compose-cleanup=PASS (down --rmi local --volumes)"
        }
        catch {
            $cleanupFailure = $_
            Add-Evidence "compose-cleanup=FAIL: $($_.Exception.Message)"
        }
    }

    $env:DCX_DATA_ROOT = $previousDataRoot
    $env:DCX_DOWNLOAD_ROOT = $previousDownloadRoot

    if (Test-Path -LiteralPath $runRoot -PathType Container) {
        $evidence | Set-Content -LiteralPath $evidenceFile -Encoding UTF8
    }
}

if ($null -ne $validationFailure) {
    throw $validationFailure
}

if ($null -ne $cleanupFailure) {
    throw $cleanupFailure
}
