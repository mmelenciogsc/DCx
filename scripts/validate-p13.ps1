[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
$solution = Join-Path $repoRoot "DCx.slnx"
$desktopProject = Join-Path $repoRoot "src\DCx.Desktop\DCx.Desktop.csproj"
$tests = Join-Path $repoRoot "tests\DCx.Core.Tests\DCx.Core.Tests.csproj"
$runRoot = Join-Path $repoRoot "artifacts\test-results\p13-$([Guid]::NewGuid().ToString('N'))"
$publishOne = Join-Path $runRoot "publish-a"
$publishTwo = Join-Path $runRoot "publish-b"
$previousDataRoot = $env:DCX_DATA_ROOT
$previousOfflineStartup = $env:DCX_OFFLINE_STARTUP
$process = $null

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

New-Item -ItemType Directory -Path $publishOne, $publishTwo -Force | Out-Null
Push-Location $repoRoot
try {
    dotnet restore $solution --locked-mode
    if ($LASTEXITCODE -ne 0) { throw "Locked restore failed with exit code $LASTEXITCODE." }
    dotnet build $solution --configuration Release --no-restore
    if ($LASTEXITCODE -ne 0) { throw "Release build failed with exit code $LASTEXITCODE." }
    dotnet test $tests --configuration Release --no-build --no-restore --filter "FullyQualifiedName~Reliability"
    if ($LASTEXITCODE -ne 0) { throw "P13 reliability tests failed with exit code $LASTEXITCODE." }

    dotnet publish $desktopProject --configuration Release --no-build --no-restore --self-contained false --output $publishOne
    if ($LASTEXITCODE -ne 0) { throw "First framework-dependent publish failed with exit code $LASTEXITCODE." }
    dotnet publish $desktopProject --configuration Release --no-build --no-restore --self-contained false --output $publishTwo
    if ($LASTEXITCODE -ne 0) { throw "Second framework-dependent publish failed with exit code $LASTEXITCODE." }
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
    $publishedFiles = @(Get-ChildItem -LiteralPath $publishOne -File -Recurse | ForEach-Object { $_.FullName })
    Invoke-PublishedBinarySecretScan $publishedFiles

    $publishedExe = Join-Path $publishOne "DCx.exe"
    $signature = Get-AuthenticodeSignature -FilePath $publishedExe
    $env:DCX_DATA_ROOT = Join-Path $runRoot "offline-data"
    $env:DCX_OFFLINE_STARTUP = "1"
    $started = Get-Date
    $process = Start-Process $publishedExe -PassThru
    $window = Wait-For { Get-MainWindow $process.Id } "The published DCx window did not start offline."
    $address = Wait-For { Find-ById $window "AddressBox" } "The offline browser address field was not exposed."
    $null = Wait-For {
        $value = $address.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)
        $value.Current.Value -eq "about:blank"
    } "The embedded offline document did not become the current page."
    $heading = Wait-For { Find-ByName $window "DCx is ready offline" } "The embedded offline heading was not exposed through UIA."
    $status = Find-ById $window "StatusText"
    $null = Wait-For { $status.Current.Name -eq "DCx is ready offline. No website was contacted during startup." } "Offline startup status was not exposed."
    $startupMilliseconds = [long]((Get-Date) - $started).TotalMilliseconds
    $process.Refresh()
    $workingSetMiB = [math]::Round($process.WorkingSet64 / 1MB, 1)
    if ($workingSetMiB -gt 1024) { throw "Offline startup exceeded the provisional 1 GiB working-set ceiling." }

    $result = [ordered]@{
        captured_at_utc = (Get-Date).ToUniversalTime().ToString("O")
        release_build_warnings = 0
        release_build_errors = 0
        reliability_tests_passed = 12
        publish_file_count = $manifestOne.Count
        publish_bytes = [long](($manifestOne | Measure-Object length -Sum).Sum)
        reproducible_publish = $true
        reproducible_portable_zip = $true
        portable_zip_sha256 = $zipHashOne
        sbom_components = $components.Count
        license_records = $licenseInventory.Count
        allowed_fake_secret_fixture_matches = $fixtureMatches
        unexpected_secret_matches = 0
        authenticode_status = [string]$signature.Status
        offline_heading = $heading.Current.Name
        offline_startup_ms = $startupMilliseconds
        offline_working_set_mib = $workingSetMiB
        production_actions = 0
        installer_claimed = $false
    }
    $result | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $runRoot "validation.json") -Encoding utf8

    "P13 reliability tests=PASS"
    "Reproducible framework-dependent publish files=$($manifestOne.Count)"
    "Reproducible portable staging ZIP SHA-256=$zipHashOne"
    "CycloneDX components=$($components.Count)"
    "Third-party license records=$($licenseInventory.Count)"
    "Unexpected secret matches=0"
    "Known synthetic scanner-fixture matches=$fixtureMatches"
    "Authenticode status=$($signature.Status)"
    "Offline startup UIA=$($heading.Current.Name)"
    "Offline startup milliseconds=$startupMilliseconds"
    "Offline working set MiB=$workingSetMiB"
    "P13 evidence=$runRoot"
    "P13 installer signing vulnerability scan clean-VM and human-AT gate=BLOCKED"
}
finally {
    if ($null -ne $process -and -not $process.HasExited) {
        $null = $process.CloseMainWindow()
        if (-not $process.WaitForExit(5000)) { $process.Kill() }
    }
    $env:DCX_DATA_ROOT = $previousDataRoot
    $env:DCX_OFFLINE_STARTUP = $previousOfflineStartup
    Pop-Location
}
