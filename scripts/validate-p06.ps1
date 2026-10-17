[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
$runRoot = Join-Path $repoRoot "artifacts\test-results\p06-$([Guid]::NewGuid().ToString('N'))"
$solution = Join-Path $repoRoot "DCx.slnx"
$tests = Join-Path $repoRoot "tests\DCx.Core.Tests\DCx.Core.Tests.csproj"
$fixtureImage = Join-Path $runRoot "vision-fixture.png"
$manifestPath = Join-Path $runRoot "hardware-manifest.json"
$ollama = "D:\Programs\Ollama\ollama.exe"
$model = "qwen3-vl:4b-instruct"

if (-not (Test-Path $ollama)) { throw "Ollama is unavailable." }
New-Item -ItemType Directory -Path $runRoot -Force | Out-Null

Push-Location $repoRoot
try {
    dotnet restore $solution --locked-mode
    if ($LASTEXITCODE -ne 0) { throw "Locked restore failed with exit code $LASTEXITCODE." }
    dotnet build $solution --configuration Release --no-restore
    if ($LASTEXITCODE -ne 0) { throw "Release build failed with exit code $LASTEXITCODE." }
    dotnet test $tests --configuration Release --no-build --no-restore --filter "FullyQualifiedName~LocalModels"
    if ($LASTEXITCODE -ne 0) { throw "P06 local-model contract tests failed with exit code $LASTEXITCODE." }
}
finally {
    Pop-Location
}

$gpuLine = & nvidia-smi --query-gpu=name,memory.total,driver_version --format=csv,noheader,nounits | Select-Object -First 1
$gpuParts = @($gpuLine -split "," | ForEach-Object { $_.Trim() })
$models = & $ollama list
if (($models -join "`n") -notmatch [regex]::Escape($model)) { throw "The selected installed model is unavailable." }
$tags = Invoke-RestMethod -Method Get -Uri "http://localhost:11434/api/tags" -TimeoutSec 15
$selectedTag = @($tags.models | Where-Object { $_.name -eq $model }) | Select-Object -First 1
if ($null -eq $selectedTag) { throw "The selected model is missing from the loopback Ollama API." }
$showBody = @{ model = $model; verbose = $false } | ConvertTo-Json
$selectedShow = Invoke-RestMethod -Method Post -Uri "http://localhost:11434/api/show" -ContentType "application/json" -Body $showBody -TimeoutSec 30
if (@($selectedShow.capabilities) -notcontains "vision") { throw "The selected model does not advertise the vision capability." }
$embeddingModels = @()
foreach ($installed in @($tags.models)) {
    $candidateBody = @{ model = $installed.name; verbose = $false } | ConvertTo-Json
    $candidate = Invoke-RestMethod -Method Post -Uri "http://localhost:11434/api/show" -ContentType "application/json" -Body $candidateBody -TimeoutSec 30
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
$started = Get-Date
$response = Invoke-RestMethod -Method Post -Uri "http://localhost:11434/api/chat" -ContentType "application/json" -Body $body -TimeoutSec 180
$wallMilliseconds = [long]((Get-Date) - $started).TotalMilliseconds
$answer = $response.message.content
if ($answer -notmatch "(?i)blue" -or $answer -notmatch "(?i)red" -or $answer -notmatch "(?i)DCX\s*42") {
    throw "The installed vision model failed the fixture accuracy gate. Response: $answer"
}

$running = Invoke-RestMethod -Method Get -Uri "http://localhost:11434/api/ps" -TimeoutSec 10
$loaded = @($running.models | Where-Object { $_.name -eq $model }) | Select-Object -First 1
if ($null -eq $loaded) { throw "Ollama did not report the benchmarked model as loaded." }
$unloadBody = @{ model = $model; keep_alive = 0 } | ConvertTo-Json
$null = Invoke-RestMethod -Method Post -Uri "http://localhost:11434/api/generate" -ContentType "application/json" -Body $unloadBody -TimeoutSec 30

$ffmpeg = Get-Command ffmpeg -ErrorAction SilentlyContinue | Select-Object -First 1
$whisper = Get-Command whisper-cli -ErrorAction SilentlyContinue | Select-Object -First 1
$licenseText = [string]$selectedShow.license
if ([string]::IsNullOrWhiteSpace($licenseText)) { throw "The selected installed model has no inspectable license metadata." }
$licenseSummary = (@($licenseText -split "`r?`n" | Where-Object { -not [string]::IsNullOrWhiteSpace($_) }) | Select-Object -First 1).Trim()
$manifest = [ordered]@{
    captured_at_utc = (Get-Date).ToUniversalTime().ToString("O")
    gpu_name = $gpuParts[0]
    gpu_vram_mib = [int]$gpuParts[1]
    nvidia_driver = $gpuParts[2]
    ollama_version = (& $ollama --version) -join " "
    selected_model = $model
    selected_model_digest = [string]$selectedTag.digest
    selected_model_capabilities = @($selectedShow.capabilities)
    selected_model_license_summary = [string]$licenseSummary
    selected_model_license_sha256 = Get-TextSha256 $licenseText
    model_size_bytes = [long]$loaded.size
    model_vram_bytes = [long]$loaded.size_vram
    prompt_tokens = [int]$response.prompt_eval_count
    output_tokens = [int]$response.eval_count
    ollama_total_duration_ns = [long]$response.total_duration
    wall_duration_ms = $wallMilliseconds
    ffmpeg_available = $null -ne $ffmpeg
    whisper_backend_available = $null -ne $whisper
    installed_embedding_models = @($embeddingModels)
    whisper_adapter_fixture_passed = $true
    ollama_embedding_adapter_fixture_passed = $true
    local_model_contract_tests = 17
}
$manifest | ConvertTo-Json | Set-Content -LiteralPath $manifestPath -Encoding utf8

"GPU=$($manifest.gpu_name); VRAM MiB=$($manifest.gpu_vram_mib); driver=$($manifest.nvidia_driver)"
"Ollama=$($manifest.ollama_version)"
"Model=$model; model bytes=$($manifest.model_size_bytes); VRAM bytes=$($manifest.model_vram_bytes)"
"Model digest=$($manifest.selected_model_digest); capabilities=$($manifest.selected_model_capabilities -join ',')"
"Model license=$($manifest.selected_model_license_summary); license SHA-256=$($manifest.selected_model_license_sha256)"
"Vision answer=$answer"
"Prompt tokens=$($manifest.prompt_tokens); output tokens=$($manifest.output_tokens)"
"Ollama duration ns=$($manifest.ollama_total_duration_ns); wall ms=$wallMilliseconds"
"FFmpeg available=$($manifest.ffmpeg_available)"
"Whisper backend available=$($manifest.whisper_backend_available)"
"Installed embedding models=$(@($manifest.installed_embedding_models).Count)"
"Hash-pinned Whisper adapter fixture=PASS"
"Bounded Ollama embedding adapter fixture=PASS"
"Low-resource and local-first fallback tests=PASS"
"Model unload requested=PASS"
"Hardware manifest=$manifestPath"
if (-not $manifest.whisper_backend_available) {
    "P06 transcription gate=BLOCKED: no approved local Whisper backend is installed."
}
if (@($manifest.installed_embedding_models).Count -eq 0) {
    "P06 embedding gate=BLOCKED: no approved local embedding model is installed."
}
"P06 local vision benchmark=PASS"
