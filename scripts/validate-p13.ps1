[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"
if ($PSVersionTable.PSEdition -ne "Core" -or $PSVersionTable.PSVersion -lt [Version]"7.4" -or
    [Runtime.InteropServices.RuntimeInformation]::FrameworkDescription -notlike ".NET 10.*") {
    throw "P13 requires supported Windows PowerShell 7.4 or newer running on .NET 10; use pwsh.exe."
}
$repoRoot = Split-Path -Parent $PSScriptRoot
$solution = Join-Path $repoRoot "DCx.slnx"
$desktopProject = Join-Path $repoRoot "src\DCx.Desktop\DCx.Desktop.csproj"
$tests = Join-Path $repoRoot "tests\DCx.Core.Tests\DCx.Core.Tests.csproj"
$msixBuilder = Join-Path $repoRoot "scripts\build-unsigned-msix.ps1"
$webView2Validator = Join-Path $repoRoot "scripts\validate-p13-webview2.ps1"
$controlledUpdateValidator = Join-Path $repoRoot "scripts\validate-p13-controlled-update-ui.ps1"
$runRoot = Join-Path $repoRoot "artifacts\test-results\p13-$([Guid]::NewGuid().ToString('N'))"
$publishOne = Join-Path $runRoot "publish-a"
$publishTwo = Join-Path $runRoot "publish-b"
$msixOne = Join-Path $runRoot "DCx-local-validation-a.msix"
$msixTwo = Join-Path $runRoot "DCx-local-validation-b.msix"
$reliabilityTrx = Join-Path $runRoot "p13-reliability.trx"
$packageTrustTrx = Join-Path $runRoot "p13-package-trust.trx"
$webView2PolicyTrx = Join-Path $runRoot "p13-webview2-policy.trx"
$previousDataRoot = $env:DCX_DATA_ROOT
$previousOfflineStartup = $env:DCX_OFFLINE_STARTUP
$process = $null
$recoveryProcess = $null

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem

function Wait-For([scriptblock]$condition, [string]$failureMessage, [int]$attempts = 50) {
    for ($attempt = 0; $attempt -lt $attempts; $attempt++) {
        $result = & $condition
        if ($result) { return $result }
        Start-Sleep -Milliseconds 400
    }
    throw $failureMessage
}

function Get-MainWindow([int]$processId) {
    $condition = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ProcessIdProperty,
        $processId)
    return [System.Windows.Automation.AutomationElement]::RootElement.FindFirst(
        [System.Windows.Automation.TreeScope]::Children,
        $condition)
}

function Get-WindowById([int]$processId, [string]$automationId) {
    $processCondition = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ProcessIdProperty,
        $processId)
    $idCondition = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::AutomationIdProperty,
        $automationId)
    $condition = New-Object System.Windows.Automation.AndCondition($processCondition, $idCondition)
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

function Get-FileManifest([string]$root) {
    return @(
        Get-ChildItem -LiteralPath $root -File -Recurse |
            ForEach-Object {
                [pscustomobject][ordered]@{
                    path = $_.FullName.Substring($root.Length).TrimStart('\').Replace('\', '/')
                    length = $_.Length
                    sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
                }
            } |
            Sort-Object path
    )
}

function Write-ReproducibleZip([string]$source, [string]$destination) {
    $file = [IO.File]::Open($destination, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
    try {
        $archive = [IO.Compression.ZipArchive]::new($file, [IO.Compression.ZipArchiveMode]::Create, $true)
        try {
            Get-ChildItem -LiteralPath $source -File -Recurse | Sort-Object FullName | ForEach-Object {
                $relative = $_.FullName.Substring($source.Length).TrimStart('\').Replace('\', '/')
                $entry = $archive.CreateEntry($relative, [IO.Compression.CompressionLevel]::Optimal)
                $entry.LastWriteTime = [DateTimeOffset]::new(1980, 1, 1, 0, 0, 0, [TimeSpan]::Zero)
                $entryOutput = $entry.Open()
                $input = [IO.File]::OpenRead($_.FullName)
                try { $input.CopyTo($entryOutput) }
                finally { $input.Dispose(); $entryOutput.Dispose() }
            }
        }
        finally { $archive.Dispose() }
    }
    finally { $file.Dispose() }
}

function Invoke-SecretScan([string[]]$paths, [string]$scope) {
    $patterns = [ordered]@{
        openai_key = 'sk-[A-Za-z0-9_-]{20,}'
        github_token = 'gh[pousr]_[A-Za-z0-9]{20,}'
        aws_access_key = 'AKIA[0-9A-Z]{16}'
        private_key = '-----BEGIN (?:RSA |EC |OPENSSH )?PRIVATE KEY-----'
        oauth_secret_assignment = '(?i)client[_-]?' + 'secret\s*[:=]\s*["''][^"'']{8,}["'']'
    }
    $fixtureSecret = 'sk-' + 'abcdefghijklmnopqrstuvwxyz123456'
    $fixturePaths = @(
        'tests/DCx.Core.Tests/Contributions/ContributionGovernanceTests.cs',
        'tests/DCx.Core.Tests/Contributions/ContributionPolicyTests.cs'
    )
    $fixtureMatches = 0
    foreach ($path in $paths) {
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { continue }
        $canonicalPath = [IO.Path]::GetFullPath($path)
        $canonicalRepo = [IO.Path]::GetFullPath($repoRoot).TrimEnd('\') + '\'
        $relative = if ($canonicalPath.StartsWith($canonicalRepo, [StringComparison]::OrdinalIgnoreCase)) {
            $canonicalPath.Substring($canonicalRepo.Length).Replace('\', '/')
        }
        else {
            $canonicalPath
        }
        $contents = Get-Content -LiteralPath $path -Raw -ErrorAction Stop
        foreach ($name in $patterns.Keys) {
            foreach ($match in [regex]::Matches($contents, $patterns[$name])) {
                if ($match.Value -eq $fixtureSecret -and $fixturePaths -contains $relative) {
                    $fixtureMatches++
                    continue
                }
                throw "Potential $name material detected in $scope at $relative. The value was not logged."
            }
        }
    }
    return $fixtureMatches
}

function Invoke-PublishedBinarySecretScan([string[]]$paths) {
    $patterns = @(
        'sk-[A-Za-z0-9_-]{20,}',
        'gh[pousr]_[A-Za-z0-9]{20,}',
        'AKIA[0-9A-Z]{16}',
        '-----BEGIN (?:RSA |EC |OPENSSH )?PRIVATE KEY-----',
        ('(?i)client[_-]?' + 'secret\s*[:=]\s*["''][^"'']{8,}["'']')
    )
    foreach ($path in $paths) {
        $contents = [Text.Encoding]::ASCII.GetString([IO.File]::ReadAllBytes($path))
        foreach ($pattern in $patterns) {
            if ([regex]::IsMatch($contents, $pattern)) {
                $relative = $path.Substring($publishOne.Length).TrimStart('\').Replace('\', '/')
                throw "Potential secret material was embedded in published file $relative. The value was not logged."
            }
        }
    }
}

function Invoke-DefenderNoRemediationScan(
    [string]$defenderPath,
    [string]$targetPath,
    [int]$timeoutMilliseconds = 300000) {
    if (-not (Test-Path -LiteralPath $defenderPath -PathType Leaf) -or
        (-not (Test-Path -LiteralPath $targetPath -PathType Leaf) -and
         -not (Test-Path -LiteralPath $targetPath -PathType Container))) {
        throw "Defender scan inputs are unavailable."
    }

    $start = [Diagnostics.ProcessStartInfo]::new($defenderPath)
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    foreach ($argument in @("-Scan", "-ScanType", "3", "-File", $targetPath, "-DisableRemediation")) {
        $null = $start.ArgumentList.Add($argument)
    }
    $trustedSystemRoot = [IO.Directory]::GetParent([Environment]::SystemDirectory).FullName
    $trustedSystemDrive = [IO.Path]::GetPathRoot($trustedSystemRoot).TrimEnd('\')
    $trustedProgramFiles = [Environment]::GetFolderPath([Environment+SpecialFolder]::ProgramFiles)
    $trustedProgramData = [Environment]::GetFolderPath([Environment+SpecialFolder]::CommonApplicationData)
    $start.Environment.Clear()
    $start.Environment["SystemDrive"] = $trustedSystemDrive
    $start.Environment["SystemRoot"] = $trustedSystemRoot
    $start.Environment["WINDIR"] = $trustedSystemRoot
    $start.Environment["ProgramData"] = $trustedProgramData
    $start.Environment["ProgramFiles"] = $trustedProgramFiles

    $scan = [Diagnostics.Process]::new()
    $scan.StartInfo = $start
    try {
        if (-not $scan.Start()) { throw "Defender scan did not start." }
        $standardOutput = $scan.StandardOutput.ReadToEndAsync()
        $standardError = $scan.StandardError.ReadToEndAsync()
        if (-not $scan.WaitForExit($timeoutMilliseconds)) {
            try { $scan.Kill($true) } catch {}
            throw "Defender scan exceeded its time limit."
        }
        $output = $standardOutput.GetAwaiter().GetResult()
        $errors = $standardError.GetAwaiter().GetResult()
        if ($output.Length -gt 65536 -or $errors.Length -gt 65536) {
            throw "Defender scan output exceeded its limit."
        }
        if ($scan.ExitCode -ne 0) {
            throw "Defender no-remediation scan did not return a clean result."
        }
    }
    finally {
        $scan.Dispose()
    }
}

function Get-NuGetVulnerabilityCount([object]$audit) {
    $count = 0
    foreach ($project in $audit.projects) {
        foreach ($framework in $project.frameworks) {
            foreach ($collectionName in @("topLevelPackages", "transitivePackages")) {
                $packagesProperty = $framework.PSObject.Properties[$collectionName]
                if ($null -eq $packagesProperty -or $null -eq $packagesProperty.Value) { continue }
                foreach ($package in $packagesProperty.Value) {
                    if ($null -eq $package) {
                        throw "NuGet vulnerability audit returned invalid package data."
                    }
                    $vulnerabilitiesProperty = $package.PSObject.Properties["vulnerabilities"]
                    if ($null -eq $vulnerabilitiesProperty -or $null -eq $vulnerabilitiesProperty.Value) {
                        throw "NuGet vulnerability audit returned an affected package without vulnerability data."
                    }
                    $packageVulnerabilityCount = 0
                    foreach ($vulnerability in $vulnerabilitiesProperty.Value) {
                        if ($null -eq $vulnerability) {
                            throw "NuGet vulnerability audit returned invalid vulnerability data."
                        }
                        $packageVulnerabilityCount++
                    }
                    if ($packageVulnerabilityCount -eq 0) {
                        throw "NuGet vulnerability audit returned an affected package without vulnerability entries."
                    }
                    $count += $packageVulnerabilityCount
                }
            }
        }
    }
    return $count
}

New-Item -ItemType Directory -Path $publishOne, $publishTwo -Force | Out-Null
Push-Location $repoRoot
try {
    dotnet restore $solution --locked-mode
    if ($LASTEXITCODE -ne 0) { throw "Locked restore failed with exit code $LASTEXITCODE." }
    dotnet restore $desktopProject --locked-mode --runtime win-x64
    if ($LASTEXITCODE -ne 0) { throw "Locked win-x64 restore failed with exit code $LASTEXITCODE." }
    dotnet build $solution --configuration Release --no-restore
    if ($LASTEXITCODE -ne 0) { throw "Release build failed with exit code $LASTEXITCODE." }
    dotnet test $tests --configuration Release --no-build --no-restore --filter "FullyQualifiedName~Reliability" `
        --logger "trx;LogFileName=p13-reliability.trx" --results-directory $runRoot
    if ($LASTEXITCODE -ne 0) { throw "P13 reliability tests failed with exit code $LASTEXITCODE." }
    [xml]$reliabilityReport = Get-Content -LiteralPath $reliabilityTrx -Raw
    $reliabilityCounters = $reliabilityReport.TestRun.ResultSummary.Counters
    $reliabilityTestsPassed = [int]$reliabilityCounters.passed
    if ($reliabilityTestsPassed -lt 1 -or
        [int]$reliabilityCounters.total -ne $reliabilityTestsPassed -or
        [int]$reliabilityCounters.executed -ne $reliabilityTestsPassed -or
        [int]$reliabilityCounters.failed -ne 0 -or
        [int]$reliabilityCounters.error -ne 0 -or
        [int]$reliabilityCounters.timeout -ne 0 -or
        [int]$reliabilityCounters.aborted -ne 0) {
        throw "P13 TRX counters did not prove that every selected reliability test passed."
    }

    dotnet test $tests --configuration Release --no-build --no-restore `
        --filter "FullyQualifiedName~Deployment.MsixPackageTrustVerifierTests" `
        --logger "trx;LogFileName=p13-package-trust.trx" --results-directory $runRoot
    if ($LASTEXITCODE -ne 0) { throw "P13 package trust tests failed with exit code $LASTEXITCODE." }
    [xml]$packageTrustReport = Get-Content -LiteralPath $packageTrustTrx -Raw
    $packageTrustCounters = $packageTrustReport.TestRun.ResultSummary.Counters
    $packageTrustTestsPassed = [int]$packageTrustCounters.passed
    if ($packageTrustTestsPassed -ne 22 -or
        [int]$packageTrustCounters.total -ne $packageTrustTestsPassed -or
        [int]$packageTrustCounters.executed -ne $packageTrustTestsPassed -or
        [int]$packageTrustCounters.failed -ne 0 -or
        [int]$packageTrustCounters.error -ne 0 -or
        [int]$packageTrustCounters.timeout -ne 0 -or
        [int]$packageTrustCounters.aborted -ne 0) {
        throw "P13 package-trust TRX counters were not exactly 22 passed and zero failed or unexecuted."
    }

    dotnet test $tests --configuration Release --no-build --no-restore `
        --filter "FullyQualifiedName~Deployment.WebView2PrerequisiteHandoffPolicyTests" `
        --logger "trx;LogFileName=p13-webview2-policy.trx" --results-directory $runRoot
    if ($LASTEXITCODE -ne 0) { throw "P13 WebView2 prerequisite policy tests failed with exit code $LASTEXITCODE." }
    [xml]$webView2PolicyReport = Get-Content -LiteralPath $webView2PolicyTrx -Raw
    $webView2PolicyCounters = $webView2PolicyReport.TestRun.ResultSummary.Counters
    $webView2PolicyTestsPassed = [int]$webView2PolicyCounters.passed
    if ($webView2PolicyTestsPassed -ne 3 -or
        [int]$webView2PolicyCounters.total -ne $webView2PolicyTestsPassed -or
        [int]$webView2PolicyCounters.executed -ne $webView2PolicyTestsPassed -or
        [int]$webView2PolicyCounters.failed -ne 0 -or
        [int]$webView2PolicyCounters.error -ne 0 -or
        [int]$webView2PolicyCounters.timeout -ne 0 -or
        [int]$webView2PolicyCounters.aborted -ne 0) {
        throw "P13 WebView2 prerequisite-policy TRX counters were not exactly 3 passed and zero failed or unexecuted."
    }

    dotnet publish $desktopProject --configuration Release --runtime win-x64 --no-restore --self-contained true --output $publishOne
    if ($LASTEXITCODE -ne 0) { throw "First self-contained x64 publish failed with exit code $LASTEXITCODE." }
    dotnet publish $desktopProject --configuration Release --runtime win-x64 --no-build --no-restore --self-contained true --output $publishTwo
    if ($LASTEXITCODE -ne 0) { throw "Second self-contained x64 publish failed with exit code $LASTEXITCODE." }

    $msixEvidenceOne = & $msixBuilder `
        -PayloadRoot $publishOne `
        -StagingRoot (Join-Path $runRoot "msix-staging-a") `
        -PackagePath $msixOne `
        -EvidencePath (Join-Path $runRoot "msix-evidence-a.json")
    $msixEvidenceTwo = & $msixBuilder `
        -PayloadRoot $publishTwo `
        -StagingRoot (Join-Path $runRoot "msix-staging-b") `
        -PackagePath $msixTwo `
        -EvidencePath (Join-Path $runRoot "msix-evidence-b.json")
    foreach ($msixEvidence in @($msixEvidenceOne, $msixEvidenceTwo)) {
        if ($msixEvidence.package_identity -ne "DCx.AgenticBrowser.LocalValidation" -or
            $msixEvidence.package_publisher -ne "CN=DCx Local Validation" -or
            $msixEvidence.package_version -ne "1.0.0.0" -or
            $msixEvidence.architecture -ne "x64" -or
            $msixEvidence.dotnet_runtime_version -ne "10.0.12" -or
            -not $msixEvidence.dotnet_self_contained -or
            -not $msixEvidence.semantic_validation -or
            -not $msixEvidence.canonical_zip_timestamps -or
            -not $msixEvidence.makeappx_unpack_validation -or
            $msixEvidence.package_authenticode_status -ne "NotSigned" -or
            $msixEvidence.webview2_runtime_bundled -or
            -not $msixEvidence.webview2_evergreen_required -or
            $msixEvidence.signing_attempted -or
            $msixEvidence.install_attempted -or
            $msixEvidence.production_actions -ne 0) {
            throw "An MSIX staging output was not self-contained, semantically valid and inert."
        }
    }
    if ($msixEvidenceOne.package_sha256 -ne $msixEvidenceTwo.package_sha256 -or
        $msixEvidenceOne.package_length -ne $msixEvidenceTwo.package_length) {
        throw "The two MSIX staging outputs were not reproducible, self-contained, semantically valid and inert."
    }
    $manifestOne = Get-FileManifest $publishOne
    $manifestTwo = Get-FileManifest $publishTwo
    $manifestDifference = Compare-Object $manifestOne $manifestTwo -Property path, length, sha256
    if (@($manifestDifference).Count -ne 0) { throw "Two clean publish outputs are not byte reproducible." }
    $manifestOne | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $runRoot "publish-manifest.json") -Encoding utf8

    $zipOne = Join-Path $runRoot "DCx-portable-a.zip"
    $zipTwo = Join-Path $runRoot "DCx-portable-b.zip"
    Write-ReproducibleZip $publishOne $zipOne
    Write-ReproducibleZip $publishTwo $zipTwo
    $zipHashOne = (Get-FileHash -LiteralPath $zipOne -Algorithm SHA256).Hash.ToLowerInvariant()
    $zipHashTwo = (Get-FileHash -LiteralPath $zipTwo -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($zipHashOne -ne $zipHashTwo) { throw "The portable staging bundles are not byte reproducible." }

    $defenderStatus = Get-MpComputerStatus
    if (-not $defenderStatus.AMServiceEnabled -or
        -not $defenderStatus.AntivirusEnabled -or
        -not $defenderStatus.AntispywareEnabled -or
        -not $defenderStatus.RealTimeProtectionEnabled -or
        $defenderStatus.DefenderSignaturesOutOfDate -or
        [string]::IsNullOrWhiteSpace([string]$defenderStatus.AntivirusSignatureVersion)) {
        throw "Microsoft Defender protection or current signature evidence is unavailable."
    }
    $programFilesRoot = [Environment]::GetFolderPath([Environment+SpecialFolder]::ProgramFiles)
    if ([string]::IsNullOrWhiteSpace($programFilesRoot)) {
        throw "The trusted Windows Program Files location is unavailable."
    }
    $defenderPath = Join-Path $programFilesRoot "Windows Defender\MpCmdRun.exe"
    if (-not (Test-Path -LiteralPath $defenderPath -PathType Leaf) -or
        ([IO.File]::GetAttributes($defenderPath) -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "The expected Microsoft Defender command is unavailable or unsafe."
    }
    $defenderSignature = Get-AuthenticodeSignature -LiteralPath $defenderPath
    if ([string]$defenderSignature.Status -ne "Valid" -or
        $null -eq $defenderSignature.SignerCertificate -or
        $defenderSignature.SignerCertificate.Subject -notlike "CN=Microsoft Windows,*") {
        throw "The Microsoft Defender command did not pass publisher trust validation."
    }
    $defenderSignerSha = [Security.Cryptography.SHA256]::HashData(
        $defenderSignature.SignerCertificate.RawData)
    $defenderSignerSha256 = [Convert]::ToHexString($defenderSignerSha).ToLowerInvariant()
    $defenderExecutableSha256 = (Get-FileHash -LiteralPath $defenderPath -Algorithm SHA256).Hash.ToLowerInvariant()
    Invoke-DefenderNoRemediationScan $defenderPath $publishOne
    Invoke-DefenderNoRemediationScan $defenderPath $zipOne
    Invoke-DefenderNoRemediationScan $defenderPath $msixOne

    $nugetAuditRaw = dotnet list $desktopProject package --vulnerable --include-transitive --format json | Out-String
    if ($LASTEXITCODE -ne 0 -or [Text.Encoding]::UTF8.GetByteCount($nugetAuditRaw) -gt 1048576) {
        throw "NuGet vulnerability audit failed or exceeded its output bound."
    }
    $nugetAudit = $nugetAuditRaw | ConvertFrom-Json -Depth 16
    if ([int]$nugetAudit.version -ne 1 -or
        [string]$nugetAudit.parameters -ne "--vulnerable --include-transitive" -or
        @($nugetAudit.projects).Count -ne 1 -or
        @($nugetAudit.sources).Count -ne 1 -or
        [string]$nugetAudit.sources[0] -ne "https://api.nuget.org/v3/index.json") {
        throw "NuGet vulnerability audit returned an unexpected schema, project count or source."
    }
    $emptyAuditFixture = '{"projects":[{"path":"fixture.csproj"}]}' | ConvertFrom-Json
    $affectedAuditFixture = '{"projects":[{"frameworks":[{"topLevelPackages":[{"vulnerabilities":[{}]}],"transitivePackages":[{"vulnerabilities":[{},{}]}]}]}]}' | ConvertFrom-Json
    if ((Get-NuGetVulnerabilityCount $emptyAuditFixture) -ne 0 -or
        (Get-NuGetVulnerabilityCount $affectedAuditFixture) -ne 3) {
        throw "NuGet vulnerability audit parser regression check failed."
    }
    $vulnerabilityCount = Get-NuGetVulnerabilityCount $nugetAudit
    if ($vulnerabilityCount -ne 0) {
        throw "NuGet vulnerability audit found one or more affected packages. Details were not logged."
    }
    $nugetAuditRaw | Set-Content -LiteralPath (Join-Path $runRoot "nuget-vulnerability-audit.json") -Encoding utf8

    $inventoryRaw = dotnet list $desktopProject package --include-transitive --format json | Out-String
    if ($LASTEXITCODE -ne 0) { throw "Package inventory failed with exit code $LASTEXITCODE." }
    Set-Content -LiteralPath (Join-Path $runRoot "package-inventory.json") -Value $inventoryRaw -Encoding utf8
    $inventory = $inventoryRaw | ConvertFrom-Json
    $framework = $inventory.projects[0].frameworks[0]
    $topLevelIds = @($framework.topLevelPackages | ForEach-Object { $_.id })
    $packages = @($framework.topLevelPackages) + @($framework.transitivePackages) |
        Sort-Object id -Unique
    $components = @()
    $licenseInventory = @()
    foreach ($package in $packages) {
        $nuspec = Join-Path $env:USERPROFILE ".nuget\packages\$($package.id.ToLowerInvariant())\$($package.resolvedVersion)\$($package.id).nuspec"
        if (-not (Test-Path -LiteralPath $nuspec)) { throw "NuGet metadata is missing for $($package.id)." }
        [xml]$metadata = Get-Content -LiteralPath $nuspec -Raw
        $licenseNode = $metadata.package.metadata.license
        $licenseValue = [string]$licenseNode.InnerText
        $licenseType = [string]$licenseNode.type
        $licenseUrl = [string]$metadata.package.metadata.licenseUrl
        if ([string]::IsNullOrWhiteSpace($licenseValue)) { throw "License metadata is missing for $($package.id)." }
        $licenses = @(
            if ($licenseType -eq "expression") {
                [ordered]@{ expression = $licenseValue }
            }
            else {
                [ordered]@{ license = [ordered]@{ name = "$($package.id) package license file: $licenseValue"; url = $licenseUrl } }
            }
        )
        $components += [ordered]@{
            type = "library"
            name = $package.id
            version = $package.resolvedVersion
            purl = "pkg:nuget/$([uri]::EscapeDataString($package.id))@$([uri]::EscapeDataString($package.resolvedVersion))"
            scope = "required"
            licenses = $licenses
            properties = @([ordered]@{ name = "dcx:dependency-kind"; value = $(if ($topLevelIds -contains $package.id) { "direct" } else { "transitive" }) })
        }
        $licenseInventory += [ordered]@{
            package = $package.id
            version = $package.resolvedVersion
            type = $licenseType
            value = $licenseValue
            url = $licenseUrl
        }
    }
    foreach ($runtimePackage in @(
        [ordered]@{
            name = "Microsoft.NETCore.App.Runtime.win-x64"
            license_files = "Licenses/dotnet-runtime-LICENSE.txt; Licenses/dotnet-runtime-THIRD-PARTY-NOTICES.txt"
        },
        [ordered]@{
            name = "Microsoft.WindowsDesktop.App.Runtime.win-x64"
            license_files = "Licenses/windows-desktop-runtime-LICENSE.txt"
        })) {
        $components += [ordered]@{
            type = "framework"
            name = $runtimePackage.name
            version = $msixEvidenceOne.dotnet_runtime_version
            purl = "pkg:nuget/$([uri]::EscapeDataString($runtimePackage.name))@$([uri]::EscapeDataString($msixEvidenceOne.dotnet_runtime_version))"
            scope = "required"
            licenses = @([ordered]@{ expression = "MIT" })
            properties = @([ordered]@{ name = "dcx:dependency-kind"; value = "self-contained-runtime" })
        }
        $licenseInventory += [ordered]@{
            package = $runtimePackage.name
            version = $msixEvidenceOne.dotnet_runtime_version
            type = "file"
            value = $runtimePackage.license_files
            url = ""
        }
    }
    $bom = [ordered]@{
        bomFormat = "CycloneDX"
        specVersion = "1.6"
        version = 1
        metadata = [ordered]@{ component = [ordered]@{ type = "application"; name = "DCx / Agentic Browser"; version = "0.0.0-development" } }
        components = $components
    }
    $bom | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath (Join-Path $runRoot "sbom.cdx.json") -Encoding utf8
    $licenseInventory | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $runRoot "third-party-license-inventory.json") -Encoding utf8

    $textExtensions = @('.cs', '.xaml', '.csproj', '.props', '.targets', '.json', '.md', '.ps1', '.sh', '.yml', '.yaml', '.xml', '.config', '.manifest')
    $trackedSource = @(Get-ChildItem -LiteralPath $repoRoot -File -Recurse | Where-Object {
        $relative = $_.FullName.Substring($repoRoot.Length).TrimStart('\')
        $segments = $relative.Split([IO.Path]::DirectorySeparatorChar)
        $isGenerated = $relative.StartsWith('.git\', [StringComparison]::OrdinalIgnoreCase) -or
            $relative.StartsWith('artifacts\', [StringComparison]::OrdinalIgnoreCase) -or
            $segments -contains 'bin' -or $segments -contains 'obj'
        -not $isGenerated -and $textExtensions -contains $_.Extension.ToLowerInvariant()
    } | ForEach-Object { $_.FullName })
    $fixtureMatches = Invoke-SecretScan $trackedSource "tracked source"
    $publishedFiles = @(Get-ChildItem -LiteralPath $publishOne -File -Recurse)
    $publishedTextExtensions = @('.json', '.config', '.xml', '.txt', '.md', '.ps1', '.cmd', '.bat', '.yml', '.yaml')
    $publishedSecretScanFiles = @($publishedFiles | Where-Object {
        $_.Name.StartsWith('DCx.', [StringComparison]::OrdinalIgnoreCase) -or
        $_.Name.Equals('DCx.exe', [StringComparison]::OrdinalIgnoreCase) -or
        $publishedTextExtensions -contains $_.Extension.ToLowerInvariant()
    } | ForEach-Object { $_.FullName })
    foreach ($requiredOwnedBinary in @('DCx.exe', 'DCx.dll', 'DCx.Core.dll')) {
        if (-not ($publishedSecretScanFiles | Where-Object {
            [IO.Path]::GetFileName($_).Equals($requiredOwnedBinary, [StringComparison]::OrdinalIgnoreCase)
        })) {
            throw "A required DCx-owned published binary was omitted from secret scanning."
        }
    }
    Invoke-PublishedBinarySecretScan $publishedSecretScanFiles

    $publishedExe = Join-Path $publishOne "DCx.exe"
    $signature = Get-AuthenticodeSignature -FilePath $publishedExe
    $coreAssembly = [Reflection.Assembly]::LoadFrom((Join-Path $publishOne "DCx.Core.dll"))
    $manifestInspectorType = $coreAssembly.GetType(
        "DCx.Core.Deployment.MsixManifestInspector", $true)
    $authenticodeProbeType = $coreAssembly.GetType(
        "DCx.Core.Deployment.WindowsPowerShellAuthenticodeProbe", $true)
    $trustVerifierType = $coreAssembly.GetType(
        "DCx.Core.Deployment.MsixPackageTrustVerifier", $true)
    $manifestInspector = [Activator]::CreateInstance($manifestInspectorType)
    $actualMsixIdentity = $manifestInspector.InspectAsync(
        $msixOne,
        [Threading.CancellationToken]::None).GetAwaiter().GetResult()
    $authenticodeConstructors = @($authenticodeProbeType.GetConstructors() | Where-Object {
        $_.GetParameters().Count -eq 2
    })
    if ($authenticodeConstructors.Count -ne 1) {
        throw "The Windows Authenticode probe constructor contract changed."
    }
    $actualSignatureProbe = $authenticodeConstructors[0].Invoke(
        [object[]]@($null, $null))
    $windowsPowerShellPath = Join-Path `
        ([Environment]::GetFolderPath([Environment+SpecialFolder]::System)) `
        "WindowsPowerShell\v1.0\powershell.exe"
    $positiveSignatureEvidence = $actualSignatureProbe.InspectAsync(
        $windowsPowerShellPath,
        [Threading.CancellationToken]::None).GetAwaiter().GetResult()
    if ($positiveSignatureEvidence.Status -cne "Valid" -or
        [string]::IsNullOrWhiteSpace($positiveSignatureEvidence.SignerSubject) -or
        $positiveSignatureEvidence.SignerSubject.Length -gt 512 -or
        $positiveSignatureEvidence.SignerSubject -cne $positiveSignatureEvidence.SignerSubject.Trim() -or
        $positiveSignatureEvidence.SignerSubject.IndexOfAny([char[]](0..31)) -ge 0 -or
        $positiveSignatureEvidence.SignerCertificateSha256 -cnotmatch '^[0-9a-f]{64}$') {
        throw "The production Windows Authenticode probe did not return bounded valid evidence for a signed Windows binary."
    }
    $actualMsixSignature = $actualSignatureProbe.InspectAsync(
        $msixOne,
        [Threading.CancellationToken]::None).GetAwaiter().GetResult()
    $trustConstructors = @($trustVerifierType.GetConstructors())
    if ($trustConstructors.Count -ne 1) {
        throw "The package trust verifier constructor contract changed."
    }
    $actualTrustVerifier = $trustConstructors[0].Invoke(
        [object[]]@($actualSignatureProbe, $manifestInspector))
    $actualMsixTrust = $actualTrustVerifier.VerifyAsync(
        $msixOne,
        [Threading.CancellationToken]::None).GetAwaiter().GetResult()
    $zeroSha256 = [string]::new([char]'0', 64)
    if ($actualMsixIdentity.PackageIdentityName -ne "DCx.AgenticBrowser.LocalValidation" -or
        $actualMsixIdentity.PublisherId -ne "CN=DCx Local Validation" -or
        $actualMsixIdentity.Version -ne "1.0.0.0" -or
        $actualMsixSignature.Status -ne "NotSigned" -or
        $null -ne $actualMsixSignature.SignerSubject -or
        $null -ne $actualMsixSignature.SignerCertificateSha256 -or
        $actualMsixTrust.SignatureValid -or
        $actualMsixTrust.PackageIdentityName -ne $actualMsixIdentity.PackageIdentityName -or
        $actualMsixTrust.PublisherId -ne $actualMsixIdentity.PublisherId -or
        $actualMsixTrust.Version -ne $actualMsixIdentity.Version -or
        $actualMsixTrust.SignerCertificateSha256 -ne $zeroSha256) {
        throw "The actual unsigned MSIX identity or real Windows trust denial was not exact."
    }
    $env:DCX_DATA_ROOT = Join-Path $runRoot "offline-data"
    $env:DCX_OFFLINE_STARTUP = "1"
    New-Item -ItemType Directory -Path (Join-Path $env:DCX_DATA_ROOT "Browser") -Force | Out-Null
    New-Item -ItemType Directory -Path (Join-Path $env:DCX_DATA_ROOT "private\owner") -Force | Out-Null
    New-Item -ItemType Directory -Path (Join-Path $env:DCX_DATA_ROOT "WebView2\Profiles\Default") -Force | Out-Null
    Set-Content -LiteralPath (Join-Path $env:DCX_DATA_ROOT "Browser\recovery-fixture.txt") -Value "original browser metadata" -NoNewline
    Set-Content -LiteralPath (Join-Path $env:DCX_DATA_ROOT "private\owner\knowledge.db") -Value "original private knowledge" -NoNewline
    Set-Content -LiteralPath (Join-Path $env:DCX_DATA_ROOT "WebView2\Profiles\Default\Cookies") -Value "original cookie" -NoNewline
    $started = Get-Date
    $process = Start-Process $publishedExe -PassThru
    $window = Wait-For { Get-MainWindow $process.Id } "The published DCx window did not start offline."
    $address = Wait-For { Find-ById $window "AddressBox" } "The offline browser address field was not exposed."
    $null = Wait-For {
        $value = $address.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)
        $value.Current.Value -eq "about:blank"
    } "The embedded offline document did not become the current page."
    $heading = Wait-For { Find-ByName $window "DCx is ready offline" } "The embedded offline heading was not exposed through UIA."
    $offlineHeadingName = $heading.Current.Name
    $status = Find-ById $window "StatusText"
    $recoveryLauncher = Wait-For { Find-ById $window "RecoveryButton" } "The main window recovery launcher was not exposed through UIA."
    $recoveryLauncherName = $recoveryLauncher.Current.Name
    $diagnosticsLauncher = Wait-For { Find-ById $window "CrashDiagnosticsButton" } "The crash diagnostics launcher was not exposed through UIA."
    $null = Wait-For { $status.Current.Name -eq "DCx is ready offline. No website was contacted during startup." } "Offline startup status was not exposed."
    $startupMilliseconds = [long]((Get-Date) - $started).TotalMilliseconds
    $process.Refresh()
    $workingSetMiB = [math]::Round($process.WorkingSet64 / 1MB, 1)
    if ($workingSetMiB -gt 1024) { throw "Offline startup exceeded the provisional 1 GiB working-set ceiling." }

    $diagnosticsLauncher.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    $diagnosticsWindow = Wait-For {
        Get-WindowById $process.Id "CrashDiagnosticsWindow"
    } "The privacy and crash diagnostics window did not open."
    $diagnosticsHeading = Wait-For { Find-ById $diagnosticsWindow "CrashDiagnosticsHeading" } "The crash diagnostics heading was not exposed through UIA."
    $diagnosticsConsent = Wait-For { Find-ById $diagnosticsWindow "CrashReportingConsentBox" } "The crash diagnostics consent control was not exposed through UIA."
    $diagnosticsSave = Wait-For { Find-ById $diagnosticsWindow "SaveCrashReportingConsentButton" } "The crash diagnostics save control was not exposed through UIA."
    $diagnosticsDelete = Wait-For { Find-ById $diagnosticsWindow "DeleteCrashReportsButton" } "The crash diagnostics purge control was not exposed through UIA."
    $diagnosticsStatus = Wait-For { Find-ById $diagnosticsWindow "CrashDiagnosticsStatus" } "The crash diagnostics live status was not exposed through UIA."
    $diagnosticsClose = Wait-For { Find-ById $diagnosticsWindow "CloseCrashDiagnosticsButton" } "The crash diagnostics close control was not exposed through UIA."
    $diagnosticsHeadingName = $diagnosticsHeading.Current.Name
    $consentToggle = $diagnosticsConsent.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern)
    if ($consentToggle.Current.ToggleState -ne [System.Windows.Automation.ToggleState]::Off) {
        throw "Local crash diagnostics did not default to unchecked."
    }
    $null = Wait-For {
        $diagnosticsStatus.Current.Name -eq "Local crash diagnostics are disabled. No crash reports are retained or sent."
    } "The default-disabled crash diagnostics state was not exposed."
    $null = Wait-For {
        $diagnosticsConsent.Current.IsEnabled -and $diagnosticsSave.Current.IsEnabled
    } "The crash diagnostics controls did not become ready after loading."

    $consentToggle.Toggle()
    $diagnosticsSave.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    $null = Wait-For {
        $diagnosticsStatus.Current.Name -eq "Local crash diagnostics are enabled. 0 of 20 crash reports are retained. Nothing is sent automatically."
    } "The explicit crash diagnostics opt-in was not applied."
    $consentPath = Join-Path $env:DCX_DATA_ROOT "reliability\crash-report-consent.json"
    $savedConsent = Get-Content -LiteralPath $consentPath -Raw | ConvertFrom-Json
    if (-not $savedConsent.enabled -or $savedConsent.noticeVersion -ne 1) {
        throw "The local crash diagnostics consent record did not reflect explicit opt-in."
    }
    $null = Wait-For {
        $diagnosticsDelete.Current.IsEnabled
    } "The crash diagnostics purge control did not become ready after opt-in."

    $diagnosticsDelete.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    $null = Wait-For {
        $diagnosticsStatus.Current.Name -eq "All retained crash reports were deleted. Local crash diagnostics remain enabled. Nothing was sent."
    } "The explicit local crash-report purge action did not complete."
    $null = Wait-For {
        $diagnosticsConsent.Current.IsEnabled -and $diagnosticsSave.Current.IsEnabled
    } "The crash diagnostics controls did not become ready after purge."
    $consentToggle.Toggle()
    $diagnosticsSave.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    $null = Wait-For {
        $diagnosticsStatus.Current.Name -eq "Local crash diagnostics are disabled. No crash reports are retained or sent."
    } "The crash diagnostics opt-out did not purge and disable reporting."
    $savedConsent = Get-Content -LiteralPath $consentPath -Raw | ConvertFrom-Json
    if ($savedConsent.enabled) { throw "The local crash diagnostics consent record remained enabled after opt-out." }
    $retainedReports = @(Get-ChildItem -LiteralPath (Join-Path $env:DCX_DATA_ROOT "reliability\crash-reports") -File)
    if ($retainedReports.Count -ne 0) { throw "Local crash reports remained after opt-out." }
    $diagnosticsClose.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()

    $recoveryProcess = Start-Process $publishedExe -ArgumentList "--recovery" -PassThru
    $recoveryWindow = Wait-For { Get-MainWindow $recoveryProcess.Id } "The recovery window did not start."
    $recoveryHeading = Wait-For { Find-ById $recoveryWindow "RecoveryHeading" } "The recovery heading was not exposed through UIA."
    $recoveryPath = Wait-For { Find-ById $recoveryWindow "RecoveryBackupPath" } "The recovery backup path was not exposed through UIA."
    $recoveryCreate = Wait-For { Find-ById $recoveryWindow "RecoveryCreateBackup" } "The create-backup action was not exposed through UIA."
    $recoveryApproval = Wait-For { Find-ById $recoveryWindow "RecoveryRestoreApproval" } "The restore approval was not exposed through UIA."
    $recoveryRestore = Wait-For { Find-ById $recoveryWindow "RecoveryRestoreBackup" } "The restore action was not exposed through UIA."
    $recoveryStatus = Wait-For { Find-ById $recoveryWindow "RecoveryStatus" } "The recovery live status was not exposed through UIA."
    $backupPath = Join-Path $runRoot "recovery-ui.dcxbackup"
    $pathValue = $recoveryPath.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)
    $pathValue.SetValue($backupPath)
    $createInvoke = $recoveryCreate.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)
    $createInvoke.Invoke()
    $null = Wait-For {
        $recoveryStatus.Current.Name.StartsWith("Backup was not created. DCx data is in use.", [StringComparison]::Ordinal)
    } "Recovery did not deny backup while the main DCx process held the data lease."
    if (Test-Path -LiteralPath $backupPath) { throw "A backup was created while DCx data was in use." }

    $null = $process.CloseMainWindow()
    if (-not $process.WaitForExit(5000)) { throw "The main DCx process did not close for recovery validation." }
    $process = $null
    $createInvoke.Invoke()
    $null = Wait-For {
        (Test-Path -LiteralPath $backupPath) -and
        $recoveryStatus.Current.Name.StartsWith("Backup complete.", [StringComparison]::Ordinal)
    } "Recovery did not create a backup after DCx released the data lease."

    Set-Content -LiteralPath (Join-Path $env:DCX_DATA_ROOT "Browser\recovery-fixture.txt") -Value "changed browser metadata" -NoNewline
    Set-Content -LiteralPath (Join-Path $env:DCX_DATA_ROOT "private\owner\knowledge.db") -Value "changed private knowledge" -NoNewline
    Set-Content -LiteralPath (Join-Path $env:DCX_DATA_ROOT "WebView2\Profiles\Default\Cookies") -Value "current cookie remains" -NoNewline
    $approvalToggle = $recoveryApproval.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern)
    if ($approvalToggle.Current.ToggleState -ne [System.Windows.Automation.ToggleState]::Off) {
        throw "Restore replacement approval did not default to unchecked."
    }
    $restoreInvoke = $recoveryRestore.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)
    $restoreInvoke.Invoke()
    $null = Wait-For {
        $recoveryStatus.Current.Name -eq "Restore was not started. Check the replacement approval box first."
    } "Recovery did not deny restore without explicit replacement approval."
    if ((Get-Content -LiteralPath (Join-Path $env:DCX_DATA_ROOT "Browser\recovery-fixture.txt") -Raw) -ne "changed browser metadata") {
        throw "An unapproved restore changed browser metadata."
    }
    $approvalToggle.Toggle()
    $restoreInvoke.Invoke()
    $null = Wait-For {
        $recoveryStatus.Current.Name.StartsWith("Restore complete.", [StringComparison]::Ordinal)
    } "Recovery did not complete the approved restore."
    if ((Get-Content -LiteralPath (Join-Path $env:DCX_DATA_ROOT "Browser\recovery-fixture.txt") -Raw) -ne "original browser metadata") {
        throw "Recovery did not restore browser metadata."
    }
    if ((Get-Content -LiteralPath (Join-Path $env:DCX_DATA_ROOT "private\owner\knowledge.db") -Raw) -ne "original private knowledge") {
        throw "Recovery did not restore private knowledge."
    }
    if ((Get-Content -LiteralPath (Join-Path $env:DCX_DATA_ROOT "WebView2\Profiles\Default\Cookies") -Raw) -ne "current cookie remains") {
        throw "Recovery changed excluded WebView2 cookie data."
    }

    $webView2MissingEvidence = & $webView2Validator `
        -ExecutablePath $publishedExe `
        -DataRoot (Join-Path $runRoot "webview2-missing-data") `
        -EvidencePath (Join-Path $runRoot "webview2-missing-validation.json")
    if (-not $webView2MissingEvidence.fixture_gate_denied_without_environment -or
        $webView2MissingEvidence.prerequisite_panel_name -ne "WebView2 Runtime required" -or
        -not $webView2MissingEvidence.prerequisite_heading_keyboard_focus -or
        $webView2MissingEvidence.official_address -ne "https://developer.microsoft.com/microsoft-edge/webview2/consumer/" -or
        -not $webView2MissingEvidence.browser_commands_disabled -or
        -not $webView2MissingEvidence.independent_content_viewer_enabled -or
        -not $webView2MissingEvidence.recheck_fail_closed -or
        -not $webView2MissingEvidence.handoff_approval_initially_unchecked -or
        -not $webView2MissingEvidence.handoff_approval_reset -or
        -not $webView2MissingEvidence.exact_official_page_handoff -or
        @($webView2MissingEvidence.distinct_handoff_access_keys).Count -ne 2 -or
        @($webView2MissingEvidence.distinct_handoff_access_keys | Select-Object -Unique).Count -ne 2 -or
        $webView2MissingEvidence.external_child_processes_started -ne 0 -or
        $webView2MissingEvidence.webview2_profile_created -or
        $webView2MissingEvidence.download_attempted -or
        $webView2MissingEvidence.install_attempted -or
        $webView2MissingEvidence.production_actions -ne 0) {
        throw "The accessible WebView2 missing-runtime gate was not exact and inert."
    }

    $controlledUpdateEvidence = & $controlledUpdateValidator `
        -ExecutablePath $publishedExe `
        -RunRoot $runRoot
    if (-not $controlledUpdateEvidence.fixture_gate_denied_without_environment -or
        -not $controlledUpdateEvidence.heading_keyboard_focus -or
        -not $controlledUpdateEvidence.exact_review_exposed -or
        $controlledUpdateEvidence.exact_review_digest_count -ne 3 -or
        @($controlledUpdateEvidence.distinct_access_keys).Count -ne 8 -or
        @($controlledUpdateEvidence.distinct_access_keys | Select-Object -Unique).Count -ne 8 -or
        -not $controlledUpdateEvidence.approval_initially_unchecked -or
        -not $controlledUpdateEvidence.denial_invocations_originated_with_keyboard_focus -or
        -not $controlledUpdateEvidence.unapproved_stage_denied_before_storage -or
        -not $controlledUpdateEvidence.approval_reset_and_replay_denied -or
        -not $controlledUpdateEvidence.activation_review_approval_initially_unchecked -or
        -not $controlledUpdateEvidence.activation_review_unapproved_denied -or
        -not $controlledUpdateEvidence.activation_review_approval_reset -or
        $controlledUpdateEvidence.activation_readiness_digest_count -ne 1 -or
        $controlledUpdateEvidence.activation_readiness_execution_permitted -or
        -not $controlledUpdateEvidence.activation_validation_approval_initially_unchecked -or
        -not $controlledUpdateEvidence.activation_validation_unapproved_denied -or
        -not $controlledUpdateEvidence.activation_validation_approval_reset -or
        $controlledUpdateEvidence.activation_validation_execution_status -ne "validation_no_op" -or
        $controlledUpdateEvidence.activation_validation_package_manager_invocations -ne 0 -or
        $controlledUpdateEvidence.activation_validation_registered -or
        $controlledUpdateEvidence.activation_validation_restart_required -or
        -not $controlledUpdateEvidence.activation_validation_staged_set_unchanged -or
        $controlledUpdateEvidence.staged_file_count -ne 2 -or
        $controlledUpdateEvidence.staged_package_count -ne 1 -or
        $controlledUpdateEvidence.staged_signed_manifest_count -ne 1 -or
        -not $controlledUpdateEvidence.atomic_published_set -or
        $controlledUpdateEvidence.published_set_directories -ne 1 -or
        $controlledUpdateEvidence.residual_incoming_directories -ne 0 -or
        $controlledUpdateEvidence.manifest_requests -ne 2 -or
        $controlledUpdateEvidence.package_requests -ne 1 -or
        $controlledUpdateEvidence.network_requests -ne 0 -or
        $controlledUpdateEvidence.execution_permitted -or
        $controlledUpdateEvidence.installer_processes_started -ne 0 -or
        $controlledUpdateEvidence.elevation_requests -ne 0 -or
        $controlledUpdateEvidence.activation_attempts -ne 0 -or
        $controlledUpdateEvidence.restart_attempts -ne 0 -or
        $controlledUpdateEvidence.production_actions -ne 0 -or
        $controlledUpdateEvidence.normal_product_source -ne "not_configured" -or
        $controlledUpdateEvidence.normal_product_manifest_requests -ne 0 -or
        $controlledUpdateEvidence.normal_product_package_requests -ne 0 -or
        $controlledUpdateEvidence.normal_product_update_state_inferred) {
        throw "The accessible controlled-update review and inert staging gate was not exact."
    }

    $result = [ordered]@{
        captured_at_utc = (Get-Date).ToUniversalTime().ToString("O")
        release_build_warnings = 0
        release_build_errors = 0
        reliability_tests_passed = $reliabilityTestsPassed
        package_trust_tests_passed = $packageTrustTestsPassed
        webview2_prerequisite_policy_tests_passed = $webView2PolicyTestsPassed
        defender_antivirus_enabled = [bool]$defenderStatus.AntivirusEnabled
        defender_realtime_enabled = [bool]$defenderStatus.RealTimeProtectionEnabled
        defender_signature_version = [string]$defenderStatus.AntivirusSignatureVersion
        defender_signature_updated_utc = ([DateTimeOffset]$defenderStatus.AntivirusSignatureLastUpdated).ToUniversalTime().ToString("O")
        defender_signatures_out_of_date = [bool]$defenderStatus.DefenderSignaturesOutOfDate
        defender_executable_sha256 = $defenderExecutableSha256
        defender_signer_certificate_sha256 = $defenderSignerSha256
        defender_publish_scan_no_remediation = $true
        defender_zip_scan_no_remediation = $true
        defender_msix_scan_no_remediation = $true
        nuget_vulnerability_audit_source = [string]$nugetAudit.sources[0]
        nuget_vulnerabilities = $vulnerabilityCount
        publish_file_count = $manifestOne.Count
        publish_bytes = [long](($manifestOne | Measure-Object length -Sum).Sum)
        dotnet_self_contained = $true
        dotnet_runtime_version = [string]$msixEvidenceOne.dotnet_runtime_version
        reproducible_publish = $true
        reproducible_portable_zip = $true
        portable_zip_sha256 = $zipHashOne
        reproducible_unsigned_msix = $true
        msix_package_identity = [string]$msixEvidenceOne.package_identity
        msix_package_publisher = [string]$msixEvidenceOne.package_publisher
        msix_package_version = [string]$msixEvidenceOne.package_version
        msix_package_length = [long]$msixEvidenceOne.package_length
        msix_package_sha256 = [string]$msixEvidenceOne.package_sha256
        msix_makeappx_version = [string]$msixEvidenceOne.makeappx_version
        msix_makeappx_sha256 = [string]$msixEvidenceOne.makeappx_sha256
        msix_makeappx_signer_certificate_sha256 = [string]$msixEvidenceOne.makeappx_signer_certificate_sha256
        msix_makeappx_semantic_and_unpack_validation = $true
        msix_authenticode_status = [string]$actualMsixSignature.Status
        msix_real_trust_denial = (-not [bool]$actualMsixTrust.SignatureValid)
        windows_authenticode_positive_probe_status = [string]$positiveSignatureEvidence.Status
        windows_authenticode_positive_probe_signer_subject = [string]$positiveSignatureEvidence.SignerSubject
        windows_authenticode_positive_probe_certificate_sha256 = [string]$positiveSignatureEvidence.SignerCertificateSha256
        signed_msix_positive_acceptance = $false
        msix_signing_attempted = $false
        msix_install_attempted = $false
        webview2_runtime_bundled = $false
        webview2_evergreen_required = $true
        webview2_missing_runtime_fixture_gate = [bool]$webView2MissingEvidence.fixture_gate_denied_without_environment
        webview2_missing_runtime_uia = [string]$webView2MissingEvidence.prerequisite_panel_name
        webview2_missing_runtime_browser_commands_disabled = [bool]$webView2MissingEvidence.browser_commands_disabled
        webview2_missing_runtime_independent_tools_enabled = [bool]$webView2MissingEvidence.independent_content_viewer_enabled
        webview2_default_browser_handoff_approval_initially_unchecked = [bool]$webView2MissingEvidence.handoff_approval_initially_unchecked
        webview2_default_browser_handoff_approval_reset = [bool]$webView2MissingEvidence.handoff_approval_reset
        webview2_default_browser_handoff = [bool]$webView2MissingEvidence.exact_official_page_handoff
        webview2_default_browser_handoff_access_keys = @($webView2MissingEvidence.distinct_handoff_access_keys)
        webview2_default_browser_handoff_external_child_processes = [int]$webView2MissingEvidence.external_child_processes_started
        webview2_missing_runtime_profile_created = [bool]$webView2MissingEvidence.webview2_profile_created
        webview2_missing_runtime_downloads = 0
        webview2_missing_runtime_installs = 0
        controlled_update_fixture_gate = [bool]$controlledUpdateEvidence.fixture_gate_denied_without_environment
        controlled_update_uia_heading_focus = [bool]$controlledUpdateEvidence.heading_keyboard_focus
        controlled_update_exact_review_digests = [int]$controlledUpdateEvidence.exact_review_digest_count
        controlled_update_access_keys = @($controlledUpdateEvidence.distinct_access_keys)
        controlled_update_approval_default_unchecked = [bool]$controlledUpdateEvidence.approval_initially_unchecked
        controlled_update_denial_invocations_originated_with_keyboard_focus = [bool]$controlledUpdateEvidence.denial_invocations_originated_with_keyboard_focus
        controlled_update_unapproved_stage_denied_before_storage = [bool]$controlledUpdateEvidence.unapproved_stage_denied_before_storage
        controlled_update_approval_reset = [bool]$controlledUpdateEvidence.approval_reset_and_replay_denied
        controlled_update_activation_review_default_unchecked = [bool]$controlledUpdateEvidence.activation_review_approval_initially_unchecked
        controlled_update_unapproved_activation_review_denied = [bool]$controlledUpdateEvidence.activation_review_unapproved_denied
        controlled_update_activation_review_reset = [bool]$controlledUpdateEvidence.activation_review_approval_reset
        controlled_update_activation_readiness_digests = [int]$controlledUpdateEvidence.activation_readiness_digest_count
        controlled_update_activation_readiness_execution_permitted = [bool]$controlledUpdateEvidence.activation_readiness_execution_permitted
        controlled_update_activation_validation_approval_default_unchecked = [bool]$controlledUpdateEvidence.activation_validation_approval_initially_unchecked
        controlled_update_unapproved_activation_validation_denied = [bool]$controlledUpdateEvidence.activation_validation_unapproved_denied
        controlled_update_activation_validation_approval_reset = [bool]$controlledUpdateEvidence.activation_validation_approval_reset
        controlled_update_activation_validation_status = [string]$controlledUpdateEvidence.activation_validation_execution_status
        controlled_update_activation_validation_package_manager_invocations = [int]$controlledUpdateEvidence.activation_validation_package_manager_invocations
        controlled_update_activation_validation_registered = [bool]$controlledUpdateEvidence.activation_validation_registered
        controlled_update_activation_validation_restart_required = [bool]$controlledUpdateEvidence.activation_validation_restart_required
        controlled_update_activation_validation_staged_set_unchanged = [bool]$controlledUpdateEvidence.activation_validation_staged_set_unchanged
        controlled_update_staged_files = [int]$controlledUpdateEvidence.staged_file_count
        controlled_update_atomic_published_set = [bool]$controlledUpdateEvidence.atomic_published_set
        controlled_update_residual_incoming_directories = [int]$controlledUpdateEvidence.residual_incoming_directories
        controlled_update_manifest_requests = [int]$controlledUpdateEvidence.manifest_requests
        controlled_update_package_requests = [int]$controlledUpdateEvidence.package_requests
        controlled_update_network_requests = [int]$controlledUpdateEvidence.network_requests
        controlled_update_installer_processes = [int]$controlledUpdateEvidence.installer_processes_started
        controlled_update_elevation_requests = [int]$controlledUpdateEvidence.elevation_requests
        controlled_update_activation_attempts = [int]$controlledUpdateEvidence.activation_attempts
        controlled_update_restart_attempts = [int]$controlledUpdateEvidence.restart_attempts
        controlled_update_normal_source = [string]$controlledUpdateEvidence.normal_product_source
        controlled_update_inferred_state = [bool]$controlledUpdateEvidence.normal_product_update_state_inferred
        sbom_components = $components.Count
        license_records = $licenseInventory.Count
        published_secret_scan_files = $publishedSecretScanFiles.Count
        allowed_fake_secret_fixture_matches = $fixtureMatches
        unexpected_secret_matches = 0
        authenticode_status = [string]$signature.Status
        offline_heading = $offlineHeadingName
        offline_startup_ms = $startupMilliseconds
        offline_working_set_mib = $workingSetMiB
        crash_diagnostics_uia_heading = $diagnosticsHeadingName
        crash_diagnostics_default_unchecked = $true
        crash_diagnostics_consent_and_purge = $true
        crash_diagnostics_network_transfers = 0
        recovery_uia_heading = $recoveryHeading.Current.Name
        recovery_launcher_uia_name = $recoveryLauncherName
        recovery_denied_while_running = $true
        recovery_backup_and_restore = $true
        recovery_restore_approval_default_unchecked = $true
        recovery_unapproved_restore_denied = $true
        recovery_excluded_cookie_unchanged = $true
        production_actions = 0
        installer_claimed = $false
    }
    $result | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $runRoot "validation.json") -Encoding utf8

    "P13 reliability tests=PASS ($reliabilityTestsPassed/$reliabilityTestsPassed)"
    "P13 bounded MSIX manifest and Windows Authenticode tests=PASS ($packageTrustTestsPassed/$packageTrustTestsPassed)"
    "P13 WebView2 prerequisite handoff policy tests=PASS ($webView2PolicyTestsPassed/$webView2PolicyTestsPassed)"
    "P13 complete unsigned local-validation MSIX trust denial=PASS"
    "P13 Microsoft Defender current signatures and trusted scanner=PASS"
    "P13 Microsoft Defender publish, ZIP and MSIX no-remediation scans=PASS"
    "P13 NuGet direct and transitive vulnerabilities=$vulnerabilityCount"
    "Reproducible self-contained x64 publish files=$($manifestOne.Count)"
    "Self-contained .NET runtime=$($msixEvidenceOne.dotnet_runtime_version)"
    "Reproducible portable staging ZIP SHA-256=$zipHashOne"
    "Reproducible semantic unsigned MSIX SHA-256=$($msixEvidenceOne.package_sha256)"
    "Unsigned MSIX MakeAppx semantic/unpack validation=PASS"
    "Unsigned MSIX real Windows trust denial=PASS ($($actualMsixSignature.Status))"
    "Windows Authenticode probe real positive evidence=PASS ($($positiveSignatureEvidence.Status))"
    "WebView2 Evergreen Runtime required and not bundled=PASS"
    "Accessible WebView2 missing-runtime detection and fail-closed recheck=PASS"
    "Separately approved exact Microsoft default-browser prerequisite handoff=PASS"
    "Accessible exact controlled-update review and one-use inert staging=PASS"
    "Accessible lease-held activation transaction validation no-op=PASS"
    "Normal controlled-update source=not_configured; inferred state=false"
    "Controlled-update network=0 installer=0 elevation=0 activation=0 restart=0 production=0"
    "CycloneDX components=$($components.Count)"
    "Third-party license records=$($licenseInventory.Count)"
    "DCx-owned binary and published text secret-scan files=$($publishedSecretScanFiles.Count)"
    "Unexpected secret matches=0"
    "Known synthetic scanner-fixture matches=$fixtureMatches"
    "Authenticode status=$($signature.Status)"
    "Offline startup UIA=$offlineHeadingName"
    "Offline startup milliseconds=$startupMilliseconds"
    "Offline working set MiB=$workingSetMiB"
    "Accessible default-off local crash diagnostics and purge=PASS"
    "Accessible recovery UIA=$($recoveryHeading.Current.Name)"
    "Closed-app backup and approved restore=PASS"
    "Backup while DCx running denied=PASS"
    "Restore without explicit approval denied=PASS"
    "Excluded WebView2 cookie unchanged=PASS"
    "P13 evidence=$runRoot"
    "P13 installer signing vulnerability scan clean-VM and human-AT gate=BLOCKED"
}
finally {
    if ($null -ne $recoveryProcess -and -not $recoveryProcess.HasExited) {
        $null = $recoveryProcess.CloseMainWindow()
        if (-not $recoveryProcess.WaitForExit(5000)) { $recoveryProcess.Kill() }
    }
    if ($null -ne $process -and -not $process.HasExited) {
        $null = $process.CloseMainWindow()
        if (-not $process.WaitForExit(5000)) { $process.Kill() }
    }
    $env:DCX_DATA_ROOT = $previousDataRoot
    $env:DCX_OFFLINE_STARTUP = $previousOfflineStartup
    Pop-Location
}
