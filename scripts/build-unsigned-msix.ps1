[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$PayloadRoot,

    [Parameter(Mandatory)]
    [string]$StagingRoot,

    [Parameter(Mandatory)]
    [string]$PackagePath,

    [Parameter(Mandatory)]
    [string]$EvidencePath)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version 3.0

$repoRoot = Split-Path -Parent $PSScriptRoot
$artifactsRoot = [IO.Path]::GetFullPath((Join-Path $repoRoot "artifacts")).TrimEnd('\')
$artifactsPrefix = $artifactsRoot + '\'
$manifestSource = Join-Path $repoRoot "packaging\msix\AppxManifest.xml"
$packageIdentity = "DCx.AgenticBrowser.LocalValidation"
$packagePublisher = "CN=DCx Local Validation"
$packageVersion = "1.0.0.0"
$fixedTimestamp = [DateTime]::new(2000, 1, 1, 0, 0, 0, [DateTimeKind]::Utc)

function Resolve-NewFilePath([string]$path, [string]$expectedExtension) {
    $fullPath = [IO.Path]::GetFullPath($path)
    if ([string]::IsNullOrWhiteSpace($fullPath) -or
        -not $fullPath.StartsWith($artifactsPrefix, [StringComparison]::OrdinalIgnoreCase) -or
        [IO.Path]::GetPathRoot($fullPath).TrimEnd('\') -eq $fullPath.TrimEnd('\') -or
        [IO.Path]::GetExtension($fullPath) -ne $expectedExtension -or
        (Test-Path -LiteralPath $fullPath)) {
        throw "The requested staging output path is invalid or already exists."
    }

    $parent = Split-Path -Parent $fullPath
    if (-not (Test-Path -LiteralPath $parent -PathType Container)) {
        throw "The requested staging output parent is unavailable or unsafe."
    }
    $current = [IO.Path]::GetFullPath($parent)
    while ($current.Length -ge $artifactsRoot.Length) {
        if (([IO.File]::GetAttributes($current) -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "The requested staging output has an unsafe reparse ancestor."
        }
        if ($current.Equals($artifactsRoot, [StringComparison]::OrdinalIgnoreCase)) { break }
        $next = Split-Path -Parent $current
        if ($next -eq $current) { throw "The requested staging output escaped its root." }
        $current = $next
    }

    return $fullPath
}

function Get-TrustedMakeAppx {
    $programFilesX86 = [Environment]::GetFolderPath([Environment+SpecialFolder]::ProgramFilesX86)
    if ([string]::IsNullOrWhiteSpace($programFilesX86)) {
        throw "The Windows SDK location is unavailable."
    }

    $path = Join-Path $programFilesX86 "Windows Kits\10\bin\10.0.19041.0\x64\makeappx.exe"
    if (-not (Test-Path -LiteralPath $path -PathType Leaf) -or
        ([IO.File]::GetAttributes($path) -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "The supported x64 MakeAppx tool is unavailable or unsafe."
    }

    $signature = Get-AuthenticodeSignature -LiteralPath $path
    if ([string]$signature.Status -ne "Valid" -or
        $null -eq $signature.SignerCertificate -or
        $signature.SignerCertificate.Subject -notlike "CN=Microsoft Corporation,*") {
        throw "The MakeAppx tool did not pass publisher trust validation."
    }

    $certificateHash = [Security.Cryptography.SHA256]::HashData(
        $signature.SignerCertificate.RawData)
    return [pscustomobject][ordered]@{
        path = $path
        version = [string](Get-Item -LiteralPath $path).VersionInfo.FileVersion
        sha256 = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
        signer_certificate_sha256 = [Convert]::ToHexString($certificateHash).ToLowerInvariant()
    }
}

function Write-DcxLogo([string]$path, [int]$size) {
    if ($size -lt 32 -or $size -gt 512) { throw "The package logo size is invalid." }
    Add-Type -AssemblyName PresentationCore | Out-Null
    Add-Type -AssemblyName WindowsBase | Out-Null
    $stride = $size * 4
    $pixels = [byte[]]::new($stride * $size)
    for ($y = 0; $y -lt $size; $y++) {
        for ($x = 0; $x -lt $size; $x++) {
            $gridX = (($x + 0.5) * 32.0) / $size
            $gridY = (($y + 0.5) * 32.0) / $size
            $red = 11
            $green = 16
            $blue = 32

            $isD = (($gridX -ge 2 -and $gridX -lt 4 -and $gridY -ge 5 -and $gridY -lt 27) -or
                ($gridX -ge 2 -and $gridX -lt 8 -and (($gridY -ge 5 -and $gridY -lt 7) -or ($gridY -ge 25 -and $gridY -lt 27))) -or
                ($gridX -ge 7 -and $gridX -lt 9 -and $gridY -ge 7 -and $gridY -lt 25))
            $isC = (($gridX -ge 11 -and $gridX -lt 13 -and $gridY -ge 5 -and $gridY -lt 27) -or
                ($gridX -ge 11 -and $gridX -lt 20 -and (($gridY -ge 5 -and $gridY -lt 7) -or ($gridY -ge 25 -and $gridY -lt 27))))
            $diagonal = (($gridY - 6.0) * 8.0) / 20.0
            $isX = ($gridY -ge 6 -and $gridY -lt 26 -and
                (([Math]::Abs(($gridX - 22.0) - $diagonal) -lt 1.15) -or
                 ([Math]::Abs(($gridX - 30.0) + $diagonal) -lt 1.15)))
            if ($isD) { $red = 248; $green = 250; $blue = 252 }
            elseif ($isC) { $red = 34; $green = 211; $blue = 238 }
            elseif ($isX) { $red = 244; $green = 114; $blue = 182 }

            $offset = ($y * $stride) + ($x * 4)
            $pixels[$offset] = [byte]$blue
            $pixels[$offset + 1] = [byte]$green
            $pixels[$offset + 2] = [byte]$red
            $pixels[$offset + 3] = 255
        }
    }

    $bitmap = [Windows.Media.Imaging.WriteableBitmap]::new(
        $size,
        $size,
        96,
        96,
        [Windows.Media.PixelFormats]::Bgra32,
        $null)
    $bitmap.WritePixels([Windows.Int32Rect]::new(0, 0, $size, $size), $pixels, $stride, 0)
    [Array]::Clear($pixels, 0, $pixels.Length)
    $encoder = [Windows.Media.Imaging.PngBitmapEncoder]::new()
    $encoder.Frames.Add([Windows.Media.Imaging.BitmapFrame]::Create($bitmap))
    $stream = [IO.File]::Open($path, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
    try { $encoder.Save($stream) }
    finally { $stream.Dispose() }
}

function Copy-RuntimeNotices([string]$payload, [object]$runtimeConfiguration) {
    $frameworks = @($runtimeConfiguration.runtimeOptions.includedFrameworks)
    $core = @($frameworks | Where-Object { $_.name -eq "Microsoft.NETCore.App" })
    $desktop = @($frameworks | Where-Object { $_.name -eq "Microsoft.WindowsDesktop.App" })
    if ($core.Count -ne 1 -or $desktop.Count -ne 1 -or
        [string]$core[0].version -ne [string]$desktop[0].version -or
        [string]::IsNullOrWhiteSpace([string]$core[0].version)) {
        throw "The self-contained runtime inventory is incomplete or inconsistent."
    }

    $runtimeVersion = [string]$core[0].version
    $packagesRoot = if (-not [string]::IsNullOrWhiteSpace($env:NUGET_PACKAGES)) {
        [IO.Path]::GetFullPath($env:NUGET_PACKAGES)
    }
    else {
        Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::UserProfile)) ".nuget\packages"
    }
    $noticeSources = [ordered]@{
        "dotnet-runtime-LICENSE.txt" = Join-Path $packagesRoot "microsoft.netcore.app.runtime.win-x64\$runtimeVersion\LICENSE.TXT"
        "dotnet-runtime-THIRD-PARTY-NOTICES.txt" = Join-Path $packagesRoot "microsoft.netcore.app.runtime.win-x64\$runtimeVersion\THIRD-PARTY-NOTICES.TXT"
        "windows-desktop-runtime-LICENSE.txt" = Join-Path $packagesRoot "microsoft.windowsdesktop.app.runtime.win-x64\$runtimeVersion\LICENSE"
    }
    $noticesRoot = Join-Path $payload "Licenses"
    if (Test-Path -LiteralPath $noticesRoot) { throw "The runtime notice destination already exists." }
    New-Item -ItemType Directory -Path $noticesRoot | Out-Null
    $noticeEvidence = @()
    foreach ($name in $noticeSources.Keys) {
        $source = $noticeSources[$name]
        if (-not (Test-Path -LiteralPath $source -PathType Leaf) -or
            ([IO.File]::GetAttributes($source) -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "A required self-contained runtime notice is unavailable or unsafe."
        }
        $destination = Join-Path $noticesRoot $name
        [IO.File]::Copy($source, $destination, $false)
        $noticeEvidence += [ordered]@{
            name = $name
            length = (Get-Item -LiteralPath $destination).Length
            sha256 = (Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash.ToLowerInvariant()
        }
    }
    return [pscustomobject][ordered]@{
        runtime_version = $runtimeVersion
        notices = $noticeEvidence
    }
}

function Set-CanonicalZipTimestamps([string]$path) {
    $stream = [IO.File]::Open($path, [IO.FileMode]::Open, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
    try {
        if ($stream.Length -lt 22 -or $stream.Length -gt 1GB) {
            throw "The package ZIP length is outside the canonicalization bounds."
        }

        $tailLength = [int][Math]::Min(65557, $stream.Length)
        $tail = [byte[]]::new($tailLength)
        $stream.Position = $stream.Length - $tailLength
        $read = 0
        while ($read -lt $tail.Length) {
            $count = $stream.Read($tail, $read, $tail.Length - $read)
            if ($count -eq 0) { throw "The package ZIP end record is truncated." }
            $read += $count
        }

        $endIndex = -1
        for ($index = $tail.Length - 22; $index -ge 0; $index--) {
            if ([BitConverter]::ToUInt32($tail, $index) -ne 0x06054b50) { continue }
            $commentLength = [BitConverter]::ToUInt16($tail, $index + 20)
            if ($index + 22 + $commentLength -eq $tail.Length) {
                $endIndex = $index
                break
            }
        }
        if ($endIndex -lt 0) { throw "The package ZIP end record is invalid." }

        $endOffset = $stream.Length - $tail.Length + $endIndex
        $diskNumber = [BitConverter]::ToUInt16($tail, $endIndex + 4)
        $centralDisk = [BitConverter]::ToUInt16($tail, $endIndex + 6)
        $entriesOnDisk = [BitConverter]::ToUInt16($tail, $endIndex + 8)
        $entryCount = [uint64][BitConverter]::ToUInt16($tail, $endIndex + 10)
        $centralLength = [uint64][BitConverter]::ToUInt32($tail, $endIndex + 12)
        $centralOffset = [uint64][BitConverter]::ToUInt32($tail, $endIndex + 16)
        $centralEnd = [uint64]$endOffset
        if ($entryCount -eq [uint16]::MaxValue -or
            $centralLength -eq [uint32]::MaxValue -or
            $centralOffset -eq [uint32]::MaxValue) {
            $locatorOffset = $endOffset - 20
            if ($locatorOffset -lt 0) { throw "The package ZIP64 locator is missing." }
            $stream.Position = $locatorOffset
            $locator = [byte[]]::new(20)
            if ($stream.Read($locator, 0, $locator.Length) -ne $locator.Length -or
                [BitConverter]::ToUInt32($locator, 0) -ne 0x07064b50 -or
                [BitConverter]::ToUInt32($locator, 4) -ne 0 -or
                [BitConverter]::ToUInt32($locator, 16) -ne 1) {
                throw "The package ZIP64 locator is invalid or multidisk."
            }
            $zip64Offset = [BitConverter]::ToUInt64($locator, 8)
            if ($zip64Offset + 56 -gt [uint64]$locatorOffset) {
                throw "The package ZIP64 end record offset is invalid."
            }
            $stream.Position = [long]$zip64Offset
            $zip64 = [byte[]]::new(56)
            if ($stream.Read($zip64, 0, $zip64.Length) -ne $zip64.Length -or
                [BitConverter]::ToUInt32($zip64, 0) -ne 0x06064b50) {
                throw "The package ZIP64 end record is invalid."
            }
            $zip64RecordLength = [BitConverter]::ToUInt64($zip64, 4)
            if ($zip64RecordLength -lt 44 -or $zip64Offset + 12 + $zip64RecordLength -ne [uint64]$locatorOffset -or
                [BitConverter]::ToUInt32($zip64, 16) -ne 0 -or
                [BitConverter]::ToUInt32($zip64, 20) -ne 0) {
                throw "The package ZIP64 end record is unsupported or multidisk."
            }
            $entriesOnDisk64 = [BitConverter]::ToUInt64($zip64, 24)
            $entryCount = [BitConverter]::ToUInt64($zip64, 32)
            $centralLength = [BitConverter]::ToUInt64($zip64, 40)
            $centralOffset = [BitConverter]::ToUInt64($zip64, 48)
            $centralEnd = $zip64Offset
            if ($entriesOnDisk64 -ne $entryCount) {
                throw "The package ZIP64 entry counts are inconsistent."
            }
        }
        elseif ($diskNumber -ne 0 -or $centralDisk -ne 0 -or $entriesOnDisk -ne $entryCount) {
            throw "The package ZIP uses an unsupported multidisk layout."
        }
        if ($entryCount -lt 1 -or $entryCount -gt 10000 -or
            $centralOffset -gt [uint64][long]::MaxValue -or $centralLength -gt [uint64][long]::MaxValue -or
            $centralOffset + $centralLength -ne $centralEnd) {
            throw "The package ZIP central-directory layout is invalid or excessive."
        }

        $fixedTime = [BitConverter]::GetBytes([uint16]0)
        $fixedDate = [BitConverter]::GetBytes([uint16]0x2821)
        $position = [long]$centralOffset
        for ($entryIndex = 0; $entryIndex -lt $entryCount; $entryIndex++) {
            $stream.Position = $position
            $header = [byte[]]::new(46)
            $read = 0
            while ($read -lt $header.Length) {
                $count = $stream.Read($header, $read, $header.Length - $read)
                if ($count -eq 0) { throw "A package ZIP central entry is truncated." }
                $read += $count
            }
            if ([BitConverter]::ToUInt32($header, 0) -ne 0x02014b50) {
                throw "A package ZIP central entry is invalid."
            }

            $nameLength = [BitConverter]::ToUInt16($header, 28)
            $extraLength = [BitConverter]::ToUInt16($header, 30)
            $commentLength = [BitConverter]::ToUInt16($header, 32)
            $nextPosition = $position + 46 + $nameLength + $extraLength + $commentLength
            if ([uint64]$nextPosition -gt $centralOffset + $centralLength) {
                throw "A package ZIP central entry exceeds its directory."
            }

            $compressedSize = [BitConverter]::ToUInt32($header, 20)
            $uncompressedSize = [BitConverter]::ToUInt32($header, 24)
            $diskStart = [BitConverter]::ToUInt16($header, 34)
            $localOffset32 = [BitConverter]::ToUInt32($header, 42)
            $localOffset = [uint64]$localOffset32
            if ($localOffset32 -eq [uint32]::MaxValue) {
                $stream.Position = $position + 46 + $nameLength
                $extra = [byte[]]::new($extraLength)
                $read = 0
                while ($read -lt $extra.Length) {
                    $count = $stream.Read($extra, $read, $extra.Length - $read)
                    if ($count -eq 0) { throw "A package ZIP64 extra field is truncated." }
                    $read += $count
                }
                $extraIndex = 0
                $foundZip64 = $false
                while ($extraIndex + 4 -le $extra.Length) {
                    $fieldId = [BitConverter]::ToUInt16($extra, $extraIndex)
                    $fieldLength = [BitConverter]::ToUInt16($extra, $extraIndex + 2)
                    $fieldStart = $extraIndex + 4
                    $fieldEnd = $fieldStart + $fieldLength
                    if ($fieldEnd -gt $extra.Length) { throw "A package ZIP extra field is invalid." }
                    if ($fieldId -eq 0x0001) {
                        $cursor = $fieldStart
                        if ($uncompressedSize -eq [uint32]::MaxValue) { $cursor += 8 }
                        if ($compressedSize -eq [uint32]::MaxValue) { $cursor += 8 }
                        if ($cursor + 8 -gt $fieldEnd) { throw "The package ZIP64 local offset is missing." }
                        $localOffset = [BitConverter]::ToUInt64($extra, $cursor)
                        $cursor += 8
                        if ($diskStart -eq [uint16]::MaxValue -and $cursor + 4 -gt $fieldEnd) {
                            throw "The package ZIP64 disk field is missing."
                        }
                        $foundZip64 = $true
                        break
                    }
                    $extraIndex = $fieldEnd
                }
                if (-not $foundZip64) { throw "The package ZIP64 local offset field is unavailable." }
            }
            if ($localOffset -gt [uint64][long]::MaxValue -or $localOffset + 30 -gt $centralOffset) {
                throw "A package ZIP local entry offset is invalid."
            }

            $stream.Position = [long]$localOffset
            $localHeader = [byte[]]::new(30)
            $read = 0
            while ($read -lt $localHeader.Length) {
                $count = $stream.Read($localHeader, $read, $localHeader.Length - $read)
                if ($count -eq 0) { throw "A package ZIP local entry is truncated." }
                $read += $count
            }
            if ([BitConverter]::ToUInt32($localHeader, 0) -ne 0x04034b50) {
                throw "A package ZIP local entry is invalid."
            }

            $stream.Position = [long]$localOffset + 10
            $stream.Write($fixedTime, 0, 2)
            $stream.Write($fixedDate, 0, 2)
            $stream.Position = $position + 12
            $stream.Write($fixedTime, 0, 2)
            $stream.Write($fixedDate, 0, 2)
            $position = $nextPosition
        }
        if ([uint64]$position -ne $centralOffset + $centralLength) {
            throw "The package ZIP central-directory length is inconsistent."
        }
        $stream.Flush($true)
    }
    finally {
        $stream.Dispose()
    }
}

function Invoke-MakeAppx(
    [string]$toolPath,
    [string[]]$arguments,
    [string]$workingDirectory) {
    $start = [Diagnostics.ProcessStartInfo]::new($toolPath)
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $start.WorkingDirectory = $workingDirectory
    foreach ($argument in $arguments) {
        $null = $start.ArgumentList.Add($argument)
    }

    $systemRoot = [IO.Directory]::GetParent([Environment]::SystemDirectory).FullName
    $start.Environment.Clear()
    $start.Environment["SystemDrive"] = [IO.Path]::GetPathRoot($systemRoot).TrimEnd('\')
    $start.Environment["SystemRoot"] = $systemRoot
    $start.Environment["WINDIR"] = $systemRoot
    $start.Environment["ProgramData"] = [Environment]::GetFolderPath([Environment+SpecialFolder]::CommonApplicationData)
    $start.Environment["ProgramFiles"] = [Environment]::GetFolderPath([Environment+SpecialFolder]::ProgramFiles)
    $start.Environment["ProgramFiles(x86)"] = [Environment]::GetFolderPath([Environment+SpecialFolder]::ProgramFilesX86)
    $temporaryRoot = Join-Path $workingDirectory "temp"
    New-Item -ItemType Directory -Path $temporaryRoot -Force | Out-Null
    $start.Environment["TEMP"] = $temporaryRoot
    $start.Environment["TMP"] = $temporaryRoot

    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $start
    try {
        if (-not $process.Start()) { throw "MakeAppx did not start." }
        $standardOutput = $process.StandardOutput.ReadToEndAsync()
        $standardError = $process.StandardError.ReadToEndAsync()
        if (-not $process.WaitForExit(300000)) {
            try { $process.Kill($true) } catch {}
            throw "MakeAppx exceeded its time limit."
        }
        $output = $standardOutput.GetAwaiter().GetResult()
        $errors = $standardError.GetAwaiter().GetResult()
        if ($output.Length -gt 131072 -or $errors.Length -gt 131072) {
            throw "MakeAppx output exceeded its limit."
        }
        if ($process.ExitCode -ne 0) {
            throw "MakeAppx package processing or semantic validation failed."
        }
    }
    finally {
        $process.Dispose()
    }
}

$payload = [IO.Path]::GetFullPath($PayloadRoot)
$staging = [IO.Path]::GetFullPath($StagingRoot)
if (-not (Test-Path -LiteralPath $payload -PathType Container) -or
    -not $payload.StartsWith($artifactsPrefix, [StringComparison]::OrdinalIgnoreCase) -or
    -not $staging.StartsWith($artifactsPrefix, [StringComparison]::OrdinalIgnoreCase) -or
    ([IO.File]::GetAttributes($payload) -band [IO.FileAttributes]::ReparsePoint) -ne 0 -or
    [IO.Path]::GetPathRoot($payload).TrimEnd('\') -eq $payload.TrimEnd('\') -or
    (Test-Path -LiteralPath $staging)) {
    throw "The self-contained payload or staging root is invalid, unsafe or already exists."
}
foreach ($candidate in @($payload, (Split-Path -Parent $staging))) {
    if (-not (Test-Path -LiteralPath $candidate -PathType Container)) {
        throw "A required staging parent is unavailable."
    }
    $current = [IO.Path]::GetFullPath($candidate)
    while ($current.Length -ge $artifactsRoot.Length) {
        if (([IO.File]::GetAttributes($current) -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "A staging path has an unsafe reparse ancestor."
        }
        if ($current.Equals($artifactsRoot, [StringComparison]::OrdinalIgnoreCase)) { break }
        $next = Split-Path -Parent $current
        if ($next -eq $current) { throw "A staging path escaped its root." }
        $current = $next
    }
}
$package = Resolve-NewFilePath $PackagePath ".msix"
$evidence = Resolve-NewFilePath $EvidencePath ".json"
$payloadPrefix = $payload.TrimEnd('\') + '\'
$stagingPrefix = $staging.TrimEnd('\') + '\'
if ($staging.StartsWith($payloadPrefix, [StringComparison]::OrdinalIgnoreCase) -or
    $payload.StartsWith($stagingPrefix, [StringComparison]::OrdinalIgnoreCase) -or
    $package.StartsWith($payloadPrefix, [StringComparison]::OrdinalIgnoreCase) -or
    $package.StartsWith($stagingPrefix, [StringComparison]::OrdinalIgnoreCase) -or
    $evidence.StartsWith($payloadPrefix, [StringComparison]::OrdinalIgnoreCase) -or
    $evidence.StartsWith($stagingPrefix, [StringComparison]::OrdinalIgnoreCase) -or
    @(Get-ChildItem -LiteralPath $payload -Force -Recurse | Where-Object {
        ($_.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0
    }).Count -ne 0) {
    throw "The staging paths overlap or the payload contains a reparse boundary."
}
New-Item -ItemType Directory -Path $staging | Out-Null

foreach ($required in @("DCx.exe", "DCx.dll", "DCx.Core.dll", "coreclr.dll", "hostfxr.dll", "DCx.runtimeconfig.json")) {
    if (-not (Test-Path -LiteralPath (Join-Path $payload $required) -PathType Leaf)) {
        throw "The payload is not a complete self-contained DCx x64 publish."
    }
}
if (Test-Path -LiteralPath (Join-Path $payload "msedgewebview2.exe")) {
    throw "The staging path unexpectedly bundled an unreviewed WebView2 Runtime."
}

$runtimeConfiguration = Get-Content -LiteralPath (Join-Path $payload "DCx.runtimeconfig.json") -Raw | ConvertFrom-Json -Depth 8
$runtimeEvidence = Copy-RuntimeNotices $payload $runtimeConfiguration
$releaseNotice = [ordered]@{
    schema_version = 1
    artifact_kind = "unsigned_local_validation_msix"
    package_identity = $packageIdentity
    package_publisher = $packagePublisher
    package_version = $packageVersion
    architecture = "x64"
    dotnet_self_contained = $true
    dotnet_runtime_version = $runtimeEvidence.runtime_version
    webview2_distribution = "evergreen_runtime_required"
    signed = $false
    installable_release_claimed = $false
    production_actions = 0
}
$releaseNotice | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $payload "DCx.Release.json") -Encoding utf8

$manifestPath = Join-Path $staging "AppxManifest.xml"
[IO.File]::Copy($manifestSource, $manifestPath, $false)
$assets = Join-Path $staging "Assets"
New-Item -ItemType Directory -Path $assets | Out-Null
Write-DcxLogo (Join-Path $assets "Square44x44Logo.png") 44
Write-DcxLogo (Join-Path $assets "StoreLogo.png") 50
Write-DcxLogo (Join-Path $assets "Square150x150Logo.png") 150

foreach ($file in Get-ChildItem -LiteralPath $payload, $staging -File -Recurse) {
    $file.LastWriteTimeUtc = $fixedTimestamp
}

$mappingPath = Join-Path $staging "mapping.txt"
$mappingLines = [Collections.Generic.List[string]]::new()
$mappingLines.Add("[Files]")
foreach ($file in Get-ChildItem -LiteralPath $payload -File -Recurse | Sort-Object FullName) {
    $relative = $file.FullName.Substring($payload.Length).TrimStart('\')
    if ($file.FullName.Contains('"') -or $relative.Contains('"')) { throw "The payload contains an unsupported path." }
    $mappingLines.Add("`"$($file.FullName)`" `"$relative`"")
}
foreach ($file in Get-ChildItem -LiteralPath $staging -File -Recurse |
    Where-Object { $_.FullName -ne $mappingPath } | Sort-Object FullName) {
    $relative = $file.FullName.Substring($staging.Length).TrimStart('\')
    if ($file.FullName.Contains('"') -or $relative.Contains('"')) { throw "The staging input contains an unsupported path." }
    $mappingLines.Add("`"$($file.FullName)`" `"$relative`"")
}
$mappingLines | Set-Content -LiteralPath $mappingPath -Encoding utf8

$makeAppx = Get-TrustedMakeAppx
Invoke-MakeAppx -toolPath $makeAppx.path `
    -arguments @("pack", "/f", $mappingPath, "/p", $package, "/no", "/h", "SHA256") `
    -workingDirectory $staging
if (-not (Test-Path -LiteralPath $package -PathType Leaf)) { throw "MakeAppx did not create the package." }
$packageFile = Get-Item -LiteralPath $package
if ($packageFile.Length -lt 1 -or $packageFile.Length -gt 1GB) { throw "The generated package size is invalid." }
Set-CanonicalZipTimestamps $package

$unpackRoot = Join-Path $staging "validated-unpack"
Invoke-MakeAppx -toolPath $makeAppx.path `
    -arguments @("unpack", "/p", $package, "/d", $unpackRoot, "/no") `
    -workingDirectory $staging
if (-not (Test-Path -LiteralPath (Join-Path $unpackRoot "AppxManifest.xml") -PathType Leaf) -or
    (Get-FileHash -LiteralPath (Join-Path $unpackRoot "AppxManifest.xml") -Algorithm SHA256).Hash -ne
        (Get-FileHash -LiteralPath $manifestPath -Algorithm SHA256).Hash -or
    (Get-FileHash -LiteralPath (Join-Path $unpackRoot "DCx.exe") -Algorithm SHA256).Hash -ne
        (Get-FileHash -LiteralPath (Join-Path $payload "DCx.exe") -Algorithm SHA256).Hash) {
    throw "The canonicalized package did not pass exact MakeAppx unpack validation."
}
[IO.Directory]::Delete($unpackRoot, $true)
$packageFile = Get-Item -LiteralPath $package
$packageSignature = Get-AuthenticodeSignature -LiteralPath $package
if ([string]$packageSignature.Status -ne "NotSigned" -or $null -ne $packageSignature.SignerCertificate) {
    throw "The local-validation package did not remain explicitly unsigned."
}

$archive = [IO.Compression.ZipFile]::OpenRead($package)
try {
    $entryNames = @($archive.Entries | ForEach-Object { $_.FullName })
    if (@($entryNames | Where-Object { $_ -eq "AppxManifest.xml" }).Count -ne 1 -or
        @($entryNames | Where-Object { $_ -eq "AppxBlockMap.xml" }).Count -ne 1 -or
        @($entryNames | Where-Object { $_ -eq "[Content_Types].xml" }).Count -ne 1 -or
        @($entryNames | Where-Object { $_ -ieq "AppxSignature.p7x" }).Count -ne 0) {
        throw "The generated package envelope is incomplete or unexpectedly signed."
    }
}
finally {
    $archive.Dispose()
}

$payloadFiles = @(Get-ChildItem -LiteralPath $payload -File -Recurse)
$result = [pscustomobject][ordered]@{
    schema_version = 1
    package_identity = $packageIdentity
    package_publisher = $packagePublisher
    package_version = $packageVersion
    architecture = "x64"
    makeappx_version = $makeAppx.version
    makeappx_sha256 = $makeAppx.sha256
    makeappx_signer_certificate_sha256 = $makeAppx.signer_certificate_sha256
    payload_files = $payloadFiles.Count
    payload_bytes = [long](($payloadFiles | Measure-Object Length -Sum).Sum)
    dotnet_self_contained = $true
    dotnet_runtime_version = $runtimeEvidence.runtime_version
    runtime_notices = $runtimeEvidence.notices
    webview2_runtime_bundled = $false
    webview2_evergreen_required = $true
    package_length = $packageFile.Length
    package_sha256 = (Get-FileHash -LiteralPath $package -Algorithm SHA256).Hash.ToLowerInvariant()
    package_authenticode_status = [string]$packageSignature.Status
    semantic_validation = $true
    canonical_zip_timestamps = $true
    makeappx_unpack_validation = $true
    signing_attempted = $false
    install_attempted = $false
    production_actions = 0
}
$result | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $evidence -Encoding utf8
$result
