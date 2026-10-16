[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
$runRoot = Join-Path $repoRoot "artifacts\test-results\p06-$([Guid]::NewGuid().ToString('N'))"
$fixtureImage = Join-Path $runRoot "vision-fixture.png"
$manifestPath = Join-Path $runRoot "hardware-manifest.json"
$ollama = "D:\Programs\Ollama\ollama.exe"
$model = "qwen3-vl:4b-instruct"

if (-not (Test-Path $ollama)) { throw "Ollama is unavailable." }
New-Item -ItemType Directory -Path $runRoot -Force | Out-Null

$gpuLine = & nvidia-smi --query-gpu=name,memory.total,driver_version --format=csv,noheader,nounits | Select-Object -First 1
$gpuParts = @($gpuLine -split "," | ForEach-Object { $_.Trim() })
$models = & $ollama list
if (($models -join "`n") -notmatch [regex]::Escape($model)) { throw "The selected installed model is unavailable." }

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
$manifest = [ordered]@{
    captured_at_utc = (Get-Date).ToUniversalTime().ToString("O")
    gpu_name = $gpuParts[0]
    gpu_vram_mib = [int]$gpuParts[1]
    nvidia_driver = $gpuParts[2]
    ollama_version = (& $ollama --version) -join " "
    selected_model = $model
    model_size_bytes = [long]$loaded.size
    model_vram_bytes = [long]$loaded.size_vram
    prompt_tokens = [int]$response.prompt_eval_count
    output_tokens = [int]$response.eval_count
    ollama_total_duration_ns = [long]$response.total_duration
    wall_duration_ms = $wallMilliseconds
    ffmpeg_available = $null -ne $ffmpeg
    whisper_backend_available = $null -ne $whisper
}
$manifest | ConvertTo-Json | Set-Content -LiteralPath $manifestPath -Encoding utf8

"GPU=$($manifest.gpu_name); VRAM MiB=$($manifest.gpu_vram_mib); driver=$($manifest.nvidia_driver)"
"Ollama=$($manifest.ollama_version)"
"Model=$model; model bytes=$($manifest.model_size_bytes); VRAM bytes=$($manifest.model_vram_bytes)"
"Vision answer=$answer"
"Prompt tokens=$($manifest.prompt_tokens); output tokens=$($manifest.output_tokens)"
"Ollama duration ns=$($manifest.ollama_total_duration_ns); wall ms=$wallMilliseconds"
"FFmpeg available=$($manifest.ffmpeg_available)"
"Whisper backend available=$($manifest.whisper_backend_available)"
"Model unload requested=PASS"
"Hardware manifest=$manifestPath"
if (-not $manifest.whisper_backend_available) {
    "P06 transcription gate=BLOCKED: no approved local Whisper backend is installed."
}
"P06 local vision benchmark=PASS"
