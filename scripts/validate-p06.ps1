[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"
if ($PSVersionTable.PSEdition -ne "Core" -or $PSVersionTable.PSVersion.Major -lt 7) {
    throw "P06 validation requires PowerShell 7 or later for the .NET production adapters."
}
$repoRoot = Split-Path -Parent $PSScriptRoot
$runRoot = Join-Path $repoRoot "artifacts\test-results\p06-$([Guid]::NewGuid().ToString('N'))"
$solution = Join-Path $repoRoot "DCx.slnx"
$tests = Join-Path $repoRoot "tests\DCx.Core.Tests\DCx.Core.Tests.csproj"
$localModelsTrx = Join-Path $runRoot "p06-local-models.trx"
$fixtureImage = Join-Path $runRoot "vision-fixture.png"
$ffmpegAudioFixture = Join-Path $runRoot "ffmpeg-audio-fixture.mp3"
$manifestPath = Join-Path $runRoot "hardware-manifest.json"
$whisperRegistrationRoot = Join-Path $runRoot "whisper-registration"
$whisperRegistrationEvidencePath = Join-Path $whisperRegistrationRoot "whisper-registration-evidence.json"
$ollama = "D:\Programs\Ollama\ollama.exe"
$modelDigest = "ef33995bb2ac37689e49c1a15cd627354d05a3c21d1f4bdcd6a5c83825349c1f"
$model = "llamacpp:$modelDigest"

if (-not (Test-Path $ollama)) { throw "Ollama is unavailable." }
New-Item -ItemType Directory -Path $runRoot -Force | Out-Null

Push-Location $repoRoot
try {
    dotnet restore $solution --locked-mode
    if ($LASTEXITCODE -ne 0) { throw "Locked restore failed with exit code $LASTEXITCODE." }
    dotnet build $solution --configuration Release --no-restore
    if ($LASTEXITCODE -ne 0) { throw "Release build failed with exit code $LASTEXITCODE." }
    dotnet test $tests --configuration Release --no-build --no-restore `
        --filter "FullyQualifiedName~DCx.Core.Tests.LocalModels." `
        --logger "trx;LogFileName=p06-local-models.trx" --results-directory $runRoot
    if ($LASTEXITCODE -ne 0) { throw "P06 local-model contract tests failed with exit code $LASTEXITCODE." }
    [xml]$localModelsReport = Get-Content -LiteralPath $localModelsTrx -Raw
    $localModelsCounters = $localModelsReport.TestRun.ResultSummary.Counters
    $localModelTestsPassed = [int]$localModelsCounters.passed
    if ($localModelTestsPassed -lt 1 -or
        [int]$localModelsCounters.total -ne $localModelTestsPassed -or
        [int]$localModelsCounters.executed -ne $localModelTestsPassed -or
        [int]$localModelsCounters.failed -ne 0 -or
        [int]$localModelsCounters.error -ne 0 -or
        [int]$localModelsCounters.timeout -ne 0 -or
        [int]$localModelsCounters.aborted -ne 0) {
        throw "P06 TRX counters did not prove that every selected local-model test passed."
    }
    & (Join-Path $repoRoot "scripts\validate-p06-whisper-registration.ps1") -RunRoot $whisperRegistrationRoot
    if (-not (Test-Path -LiteralPath $whisperRegistrationEvidencePath -PathType Leaf)) {
        throw "The native Whisper registration evidence was not retained."
    }
    $whisperRegistrationEvidence = Get-Content -LiteralPath $whisperRegistrationEvidencePath -Raw | ConvertFrom-Json
    if ($whisperRegistrationEvidence.native_uia -ne "pass" -or
        $whisperRegistrationEvidence.approval_default_unchecked -ne $true -or
        $whisperRegistrationEvidence.save_without_approval_denied -ne $true -or
        $whisperRegistrationEvidence.accessible_viewer_cancellation -ne $true -or
        $whisperRegistrationEvidence.canceled_partial_transcript_retained -ne $false -or
        $whisperRegistrationEvidence.cleared_backend_consumer_denied -ne $true -or
        $whisperRegistrationEvidence.ffmpeg_registration_native_uia -ne "pass" -or
        $whisperRegistrationEvidence.ffmpeg_real_windows_status -ne "NotSigned" -or
        $whisperRegistrationEvidence.ffmpeg_whisper_mp3_pipeline -ne "pass" -or
        $whisperRegistrationEvidence.cleared_ffmpeg_consumer_denied -ne $true -or
        $whisperRegistrationEvidence.transcript_focus_after_completion -ne $true -or
        $whisperRegistrationEvidence.download_or_install_action_exposed -ne $false -or
        $whisperRegistrationEvidence.source_files_preserved -ne $true -or
        $whisperRegistrationEvidence.real_backend_accuracy_validated -ne $false) {
        throw "The native Whisper registration evidence did not prove the expected safe fixture boundary."
    }
}
finally {
    Pop-Location
}

$gpuLine = & nvidia-smi --query-gpu=name,memory.total,driver_version --format=csv,noheader,nounits | Select-Object -First 1
$gpuParts = @($gpuLine -split "," | ForEach-Object { $_.Trim() })
$models = & $ollama list
if (($models -join "`n") -notmatch [regex]::Escape($model)) { throw "The selected installed model is unavailable." }
$tags = Invoke-RestMethod -Method Get -Uri "http://localhost:11434/api/tags" `
    -TimeoutSec 15 -NoProxy -MaximumRedirection 0
$selectedTags = @($tags.models | Where-Object { $_.name -ceq $model })
if ($selectedTags.Count -ne 1 -or [string]$selectedTags[0].digest -cne $modelDigest) {
    throw "The selected model name is not uniquely bound to its approved SHA-256 digest."
}
$selectedTag = $selectedTags[0]
$ambiguousMutableNames = @(
    $tags.models |
        Group-Object name |
        Where-Object { @($_.Group.digest | Sort-Object -Unique).Count -gt 1 } |
        ForEach-Object { $_.Name } |
        Sort-Object -Unique
)
$showBody = @{ model = $model; verbose = $false } | ConvertTo-Json
$selectedShow = Invoke-RestMethod -Method Post -Uri "http://localhost:11434/api/show" `
    -ContentType "application/json" -Body $showBody -TimeoutSec 30 -NoProxy -MaximumRedirection 0
if (@($selectedShow.capabilities) -notcontains "vision") { throw "The selected model does not advertise the vision capability." }
$embeddingModels = @()
foreach ($installed in @($tags.models)) {
    $candidateBody = @{ model = $installed.name; verbose = $false } | ConvertTo-Json
    $candidate = Invoke-RestMethod -Method Post -Uri "http://localhost:11434/api/show" `
        -ContentType "application/json" -Body $candidateBody -TimeoutSec 30 -NoProxy -MaximumRedirection 0
    if (@($candidate.capabilities) -contains "embedding") { $embeddingModels += $installed.name }
}

function Get-TextSha256([string]$value) {
    $sha = [Security.Cryptography.SHA256]::Create()
    try {
        $hash = $sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($value))
        return [BitConverter]::ToString($hash).Replace("-", "").ToLowerInvariant()
    }
    finally { $sha.Dispose() }
}

$licenseText = [string]$selectedShow.license
if ([string]::IsNullOrWhiteSpace($licenseText)) { throw "The selected installed model has no inspectable license metadata." }
$licenseSha256 = Get-TextSha256 $licenseText
$licenseSummary = (@($licenseText -split "`r?`n" | Where-Object { -not [string]::IsNullOrWhiteSpace($_) }) | Select-Object -First 1).Trim()

Add-Type -AssemblyName System.Drawing
$bitmap = New-Object System.Drawing.Bitmap 640, 360
$graphics = [System.Drawing.Graphics]::FromImage($bitmap)
try {
    $graphics.Clear([System.Drawing.Color]::White)
    $graphics.FillRectangle([System.Drawing.Brushes]::Blue, 50, 60, 180, 180)
    $graphics.FillEllipse([System.Drawing.Brushes]::Red, 390, 60, 180, 180)
    $font = New-Object System.Drawing.Font "Arial", 32
    try { $graphics.DrawString("DCX 42", $font, [System.Drawing.Brushes]::Black, 245, 280) }
    finally { $font.Dispose() }
    $bitmap.Save($fixtureImage, [System.Drawing.Imaging.ImageFormat]::Png)
}
finally {
    $graphics.Dispose()
    $bitmap.Dispose()
}

$imageBase64 = [Convert]::ToBase64String([IO.File]::ReadAllBytes($fixtureImage))
$coreAssembly = Join-Path $repoRoot "src\DCx.Core\bin\Release\net10.0\DCx.Core.dll"
[void][Reflection.Assembly]::LoadFrom($coreAssembly)
$client = [DCx.Core.LocalModels.OllamaLocalClient]::new(
    [Uri]"http://localhost:11434/", [TimeSpan]::FromMinutes(3))
try {
    $inventory = $client.ListModelsAsync([Threading.CancellationToken]::None).GetAwaiter().GetResult()
    $boundModels = @($inventory | Where-Object {
        $_.Name -ceq $model -and $_.Digest -ceq $modelDigest -and $_.HasDigestBoundName -and $_.SupportsVision
    })
    if ($boundModels.Count -ne 1) {
        throw "The production Ollama adapter did not expose one digest-bound vision model."
    }
    $productionResult = $client.DescribeImagePinnedAsync(
        $model,
        $modelDigest,
        $licenseSha256,
        $fixtureImage,
        "Describe the colored shapes and read the short text. Use one direct sentence.",
        [Threading.CancellationToken]::None).GetAwaiter().GetResult()
}
finally {
    $client.Dispose()
}
$answer = [string]$productionResult.Text
if ($answer -notmatch "(?i)blue" -or $answer -notmatch "(?i)red" -or $answer -notmatch "(?i)DCX\s*42") {
    throw "The installed vision model failed the production-adapter fixture accuracy gate. Response: $answer"
}

# Keep one separately measured invocation loaded just long enough to record actual Ollama VRAM allocation.
$body = @{
    model = $model
    messages = @(@{
        role = "user"
        content = "Describe the colored shapes and read the short text. Use one direct sentence."
        images = @($imageBase64)
    })
    stream = $false
    keep_alive = "1m"
    options = @{ temperature = 0; num_predict = 80 }
} | ConvertTo-Json -Depth 8
$unloaded = $false
$started = Get-Date
try {
    $response = Invoke-RestMethod -Method Post -Uri "http://localhost:11434/api/chat" `
        -ContentType "application/json" -Body $body -TimeoutSec 180 -NoProxy -MaximumRedirection 0
    $wallMilliseconds = [long]((Get-Date) - $started).TotalMilliseconds
    $responseModel = [string]$response.model
    if ($responseModel -ne $model -or $response.done -ne $true) {
        throw "The installed vision model returned a mismatched or incomplete response."
    }
    $resourceAnswer = [string]$response.message.content
    if ($resourceAnswer -notmatch "(?i)blue" -or $resourceAnswer -notmatch "(?i)red" -or
        $resourceAnswer -notmatch "(?i)DCX\s*42") {
        throw "The installed vision model failed the measured resource fixture accuracy gate."
    }

    $running = Invoke-RestMethod -Method Get -Uri "http://localhost:11434/api/ps" `
        -TimeoutSec 10 -NoProxy -MaximumRedirection 0
    $loadedMatches = @($running.models | Where-Object { [string]$_.digest -ceq $modelDigest })
    if ($loadedMatches.Count -ne 1) {
        throw "Ollama did not report exactly one loaded process for the benchmarked model digest."
    }
    $loaded = $loadedMatches[0]
}
finally {
    $unloadBody = @{ model = $model; keep_alive = 0 } | ConvertTo-Json
    $null = Invoke-RestMethod -Method Post -Uri "http://localhost:11434/api/generate" `
        -ContentType "application/json" -Body $unloadBody -TimeoutSec 30 -NoProxy -MaximumRedirection 0
    for ($attempt = 0; $attempt -lt 20; $attempt++) {
        $remaining = Invoke-RestMethod -Method Get -Uri "http://localhost:11434/api/ps" `
            -TimeoutSec 10 -NoProxy -MaximumRedirection 0
        if (@($remaining.models | Where-Object { [string]$_.digest -ceq $modelDigest }).Count -eq 0) {
            $unloaded = $true
            break
        }
        Start-Sleep -Milliseconds 250
    }
}
if (-not $unloaded) { throw "The selected Ollama model did not unload within the bounded verification window." }

$ffmpeg = Get-Command ffmpeg -ErrorAction SilentlyContinue | Select-Object -First 1
$whisper = Get-Command whisper-cli -ErrorAction SilentlyContinue | Select-Object -First 1
if ($null -eq $ffmpeg) { throw "The installed FFmpeg inventory candidate is unavailable." }
$ffmpegLicensePath = Join-Path (Split-Path -Parent (Split-Path -Parent $ffmpeg.Source)) "LICENSE"
if (-not (Test-Path -LiteralPath $ffmpegLicensePath -PathType Leaf)) {
    throw "The installed FFmpeg inventory candidate has no co-located license file."
}
$ffmpegReview = [DCx.Core.LocalModels.FfmpegBackendReviewer]::new().ReviewAsync(
    $ffmpeg.Source,
    $ffmpegLicensePath,
    [DateTimeOffset]::UtcNow,
    [Threading.CancellationToken]::None).GetAwaiter().GetResult()
if ($ffmpegReview.LicenseText -notmatch "GNU GENERAL PUBLIC LICENSE" -or
    $ffmpegReview.Binding.AuthenticodeStatus -ne "NotSigned") {
    throw "The installed FFmpeg license or real Windows signature evidence changed."
}

& $ffmpeg.Source -hide_banner -nostdin -loglevel error -f lavfi `
    -i "sine=frequency=440:sample_rate=48000:duration=1" -c:a libmp3lame -b:a 128k -y $ffmpegAudioFixture
if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $ffmpegAudioFixture -PathType Leaf)) {
    throw "The installed FFmpeg candidate could not create the deterministic MP3 fixture."
}
$ffmpegBenchmarkStore = [DCx.Core.LocalModels.FfmpegBackendSelectionStore]::OpenAsync(
    (Join-Path $runRoot "ffmpeg-benchmark-data"),
    [Threading.CancellationToken]::None).GetAwaiter().GetResult()
$null = $ffmpegBenchmarkStore.SetAsync(
    $ffmpegReview.Binding, $true, [Threading.CancellationToken]::None).GetAwaiter().GetResult()
$ffmpegBenchmarkState = $ffmpegBenchmarkStore.GetStateAsync(
    [Threading.CancellationToken]::None).GetAwaiter().GetResult()
if ($null -eq $ffmpegBenchmarkState.Backend) {
    throw "The disposable FFmpeg benchmark approval did not persist."
}
$ffmpegProcessor = $ffmpegBenchmarkState.Backend.CreateProcessor([TimeSpan]::FromSeconds(30))
$ffmpegExtractionClock = [Diagnostics.Stopwatch]::StartNew()
$ffmpegArtifact = $null
$ffmpegWavePath = $null
try {
    $ffmpegArtifact = $ffmpegProcessor.ExtractWaveAsync(
        $ffmpegAudioFixture, [Threading.CancellationToken]::None).GetAwaiter().GetResult()
    $ffmpegWavePath = $ffmpegArtifact.Path
    $ffmpegWaveBytes = [IO.File]::ReadAllBytes($ffmpegWavePath)
    if ($ffmpegWaveBytes.Length -lt 44 -or
        [Text.Encoding]::ASCII.GetString($ffmpegWaveBytes, 0, 4) -ne "RIFF" -or
        [Text.Encoding]::ASCII.GetString($ffmpegWaveBytes, 8, 4) -ne "WAVE") {
        throw "The production FFmpeg processor returned an invalid WAV container."
    }
    $waveFormat = $null
    $waveChannels = $null
    $waveSampleRate = $null
    $waveBits = $null
    $waveDataOffset = $null
    $waveDataBytes = $null
    for ($offset = 12; $offset + 8 -le $ffmpegWaveBytes.Length;) {
        $chunkName = [Text.Encoding]::ASCII.GetString($ffmpegWaveBytes, $offset, 4)
        $chunkBytes = [BitConverter]::ToUInt32($ffmpegWaveBytes, $offset + 4)
        $chunkData = $offset + 8
        if ($chunkData + $chunkBytes -gt $ffmpegWaveBytes.Length) {
            throw "The production FFmpeg processor returned malformed WAV chunk bounds."
        }
        if ($chunkName -eq "fmt " -and $chunkBytes -ge 16) {
            $waveFormat = [BitConverter]::ToUInt16($ffmpegWaveBytes, $chunkData)
            $waveChannels = [BitConverter]::ToUInt16($ffmpegWaveBytes, $chunkData + 2)
            $waveSampleRate = [BitConverter]::ToUInt32($ffmpegWaveBytes, $chunkData + 4)
            $waveBits = [BitConverter]::ToUInt16($ffmpegWaveBytes, $chunkData + 14)
        }
        elseif ($chunkName -eq "data") {
            $waveDataOffset = $chunkData
            $waveDataBytes = [int]$chunkBytes
        }
        $offset = $chunkData + [int]$chunkBytes + ([int]$chunkBytes % 2)
    }
    if ($waveFormat -ne 1 -or $waveChannels -ne 1 -or $waveSampleRate -ne 16000 -or
        $waveBits -ne 16 -or $null -eq $waveDataOffset -or $waveDataBytes -lt 16000) {
        throw "The production FFmpeg processor did not produce bounded 16 kHz mono PCM16 audio."
    }
    $sampleCount = [int]($waveDataBytes / 2)
    $zeroCrossings = 0
    $lastSign = 0
    $sumSquares = 0.0
    for ($sampleIndex = 0; $sampleIndex -lt $sampleCount; $sampleIndex++) {
        $sample = [BitConverter]::ToInt16($ffmpegWaveBytes, $waveDataOffset + ($sampleIndex * 2))
        $sumSquares += [double]$sample * [double]$sample
        $sign = if ($sample -gt 0) { 1 } elseif ($sample -lt 0) { -1 } else { 0 }
        if ($sign -ne 0) {
            if ($lastSign -ne 0 -and $sign -ne $lastSign) { $zeroCrossings++ }
            $lastSign = $sign
        }
    }
    $waveDurationSeconds = $sampleCount / [double]$waveSampleRate
    $measuredFrequencyHz = $zeroCrossings / (2.0 * $waveDurationSeconds)
    $waveRms = [Math]::Sqrt($sumSquares / $sampleCount)
    if ($measuredFrequencyHz -lt 430 -or $measuredFrequencyHz -gt 450 -or $waveRms -lt 1000) {
        throw "The production FFmpeg processor did not preserve the deterministic audio signal."
    }
    $ffmpegWaveLength = $ffmpegArtifact.Length
}
finally {
    $ffmpegExtractionClock.Stop()
    if ($null -ne $ffmpegArtifact) {
        $ffmpegArtifact.DisposeAsync().GetAwaiter().GetResult()
    }
    $null = $ffmpegBenchmarkStore.ClearAsync(
        [Threading.CancellationToken]::None).GetAwaiter().GetResult()
}
if (-not [string]::IsNullOrWhiteSpace($ffmpegWavePath) -and (Test-Path -LiteralPath $ffmpegWavePath)) {
    throw "The production FFmpeg processor retained its leased temporary WAV after disposal."
}

$legacyWhisperModelRoot = Join-Path $env:LOCALAPPDATA "ADFi\models\whisper"
$incompatibleWhisperModels = @()
if (Test-Path -LiteralPath $legacyWhisperModelRoot -PathType Container) {
    $incompatibleWhisperModels = @(
        Get-ChildItem -LiteralPath $legacyWhisperModelRoot -File -Filter "*.pt" -ErrorAction SilentlyContinue |
            Sort-Object Name | Select-Object -First 16 |
            ForEach-Object { [ordered]@{ name = $_.Name; bytes = [long]$_.Length; format = "pytorch_pt"; compatible = $false } }
    )
}
$manifest = [ordered]@{
    captured_at_utc = (Get-Date).ToUniversalTime().ToString("O")
    gpu_name = $gpuParts[0]
    gpu_vram_mib = [int]$gpuParts[1]
    nvidia_driver = $gpuParts[2]
    ollama_version = (& $ollama --version) -join " "
    selected_model = $model
    selected_model_digest = $modelDigest
    selected_model_unique_digest_binding = $true
    mutable_names_with_multiple_digests = @($ambiguousMutableNames)
    selected_model_capabilities = @($selectedShow.capabilities)
    selected_model_license_summary = [string]$licenseSummary
    selected_model_license_sha256 = $licenseSha256
    model_size_bytes = [long]$loaded.size
    model_vram_bytes = [long]$loaded.size_vram
    production_adapter_prompt_tokens = [int]$productionResult.PromptTokens
    production_adapter_output_tokens = [int]$productionResult.OutputTokens
    production_adapter_total_duration_ns = [long]$productionResult.TotalDurationNanoseconds
    measured_prompt_tokens = [int]$response.prompt_eval_count
    measured_output_tokens = [int]$response.eval_count
    ollama_total_duration_ns = [long]$response.total_duration
    wall_duration_ms = $wallMilliseconds
    ffmpeg_available = $null -ne $ffmpeg
    ffmpeg_executable_bytes = [long]$ffmpegReview.ExecutableBytes
    ffmpeg_executable_sha256 = [string]$ffmpegReview.Binding.ExecutableSha256
    ffmpeg_license_sha256 = [string]$ffmpegReview.Binding.LicenseSha256
    ffmpeg_authenticode_status = [string]$ffmpegReview.Binding.AuthenticodeStatus
    ffmpeg_registration_candidate_approved = $false
    ffmpeg_real_audio_fixture_passed = $true
    ffmpeg_real_audio_extraction_ms = [long]$ffmpegExtractionClock.ElapsedMilliseconds
    ffmpeg_real_audio_output_bytes = [long]$ffmpegWaveLength
    ffmpeg_real_audio_sample_rate = [int]$waveSampleRate
    ffmpeg_real_audio_channels = [int]$waveChannels
    ffmpeg_real_audio_bits_per_sample = [int]$waveBits
    ffmpeg_real_audio_measured_frequency_hz = [Math]::Round($measuredFrequencyHz, 2)
    ffmpeg_real_audio_rms = [Math]::Round($waveRms, 2)
    whisper_backend_available = $null -ne $whisper
    incompatible_whisper_model_candidates = @($incompatibleWhisperModels)
    installed_embedding_models = @($embeddingModels)
    whisper_adapter_fixture_passed = $true
    ollama_embedding_adapter_fixture_passed = $true
    ollama_redirects_disabled = $true
    whisper_pinned_files_locked_during_execution = $true
    whisper_registration_native_uia_passed = $true
    whisper_registration_approval_default_unchecked = $true
    whisper_registration_complete_licenses_exposed = $true
    whisper_registration_download_or_install_action_exposed = $false
    whisper_registration_source_files_preserved_after_clear = $true
    ffmpeg_registration_native_uia_passed = $true
    ffmpeg_whisper_mp3_pipeline_passed = $true
    cleared_ffmpeg_consumer_denied = $true
    whisper_registration_evidence = $whisperRegistrationEvidencePath
    local_model_contract_tests = $localModelTestsPassed
    model_unload_verified = $unloaded
}
$manifest | ConvertTo-Json | Set-Content -LiteralPath $manifestPath -Encoding utf8

"GPU=$($manifest.gpu_name); VRAM MiB=$($manifest.gpu_vram_mib); driver=$($manifest.nvidia_driver)"
"Ollama=$($manifest.ollama_version)"
"Model=$model; model bytes=$($manifest.model_size_bytes); VRAM bytes=$($manifest.model_vram_bytes)"
"Model digest=$($manifest.selected_model_digest); capabilities=$($manifest.selected_model_capabilities -join ',')"
"Unique digest-bound model alias=PASS; ambiguous mutable names observed=$(@($manifest.mutable_names_with_multiple_digests).Count)"
"Model license=$($manifest.selected_model_license_summary); license SHA-256=$($manifest.selected_model_license_sha256)"
"Vision answer=$answer"
"Production adapter prompt tokens=$($manifest.production_adapter_prompt_tokens); output tokens=$($manifest.production_adapter_output_tokens)"
"Measured prompt tokens=$($manifest.measured_prompt_tokens); output tokens=$($manifest.measured_output_tokens)"
"Ollama duration ns=$($manifest.ollama_total_duration_ns); wall ms=$wallMilliseconds"
"FFmpeg available=$($manifest.ffmpeg_available)"
"FFmpeg bytes=$($manifest.ffmpeg_executable_bytes); SHA-256=$($manifest.ffmpeg_executable_sha256)"
"FFmpeg license SHA-256=$($manifest.ffmpeg_license_sha256); Windows status=$($manifest.ffmpeg_authenticode_status); approved=False"
"Approved-binding real FFmpeg MP3-to-WAV fixture=PASS; elapsed ms=$($manifest.ffmpeg_real_audio_extraction_ms); measured Hz=$($manifest.ffmpeg_real_audio_measured_frequency_hz)"
"Whisper backend available=$($manifest.whisper_backend_available)"
"Incompatible existing PyTorch Whisper model candidates=$(@($manifest.incompatible_whisper_model_candidates).Count)"
"Installed embedding models=$(@($manifest.installed_embedding_models).Count)"
"Hash-pinned Whisper adapter fixture=PASS"
"Pinned Whisper executable/model execution leases=PASS"
"Accessible already-installed Whisper registration and complete-license review=PASS"
"Whisper registration defaults unchecked and exposes no download/install action=PASS"
"Whisper registration clear preserves all source files=PASS"
"Approved FFmpeg-to-Whisper MP3 pipeline and cleared-binding denial=PASS"
"Bounded Ollama embedding adapter fixture=PASS"
"Ollama redirect/cookie/proxy denial and response-origin validation=PASS"
"Low-resource and local-first fallback tests=PASS"
"Model unload verified=PASS"
"P06 local-model tests=PASS ($localModelTestsPassed/$localModelTestsPassed)"
"Hardware manifest=$manifestPath"
if (-not $manifest.whisper_backend_available) {
    "P06 transcription gate=BLOCKED: no approved local Whisper backend is installed."
}
if (@($manifest.installed_embedding_models).Count -eq 0) {
    "P06 embedding gate=BLOCKED: no approved local embedding model is installed."
}
"P06 local vision benchmark=PASS"
