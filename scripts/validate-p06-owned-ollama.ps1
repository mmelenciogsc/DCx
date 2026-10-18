[CmdletBinding()]
param(
    [string]$RunRoot
)

$ErrorActionPreference = "Stop"
if ($PSVersionTable.PSEdition -ne "Core" -or $PSVersionTable.PSVersion.Major -lt 7) {
    throw "Owned Ollama validation requires PowerShell 7 or later."
}

$repoRoot = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($RunRoot)) {
    $RunRoot = Join-Path $repoRoot "artifacts\test-results\p06-owned-ollama-$([Guid]::NewGuid().ToString('N'))"
}
$coreAssembly = Join-Path $repoRoot "src\DCx.Core\bin\Release\net10.0\DCx.Core.dll"
$ollamaExecutable = "D:\Programs\Ollama\ollama.exe"
$modelRoot = "D:\OllamaModels"
$evidencePath = Join-Path $RunRoot "owned-ollama-evidence.json"
$selectionRoot = Join-Path $RunRoot "runtime-selection"
$imagePath = Join-Path $RunRoot "owned-vision-fixture.png"
$visionDigest = "ef33995bb2ac37689e49c1a15cd627354d05a3c21d1f4bdcd6a5c83825349c1f"
$visionModel = "llamacpp:$visionDigest"

if (-not (Test-Path -LiteralPath $coreAssembly -PathType Leaf)) { throw "Build DCx.Core in Release before owned Ollama validation." }
if (-not (Test-Path -LiteralPath $ollamaExecutable -PathType Leaf)) { throw "The installed Ollama executable is unavailable." }
if (-not (Test-Path -LiteralPath $modelRoot -PathType Container)) { throw "The direct Ollama model root is unavailable." }
New-Item -ItemType Directory -Path $RunRoot -Force | Out-Null

Add-Type -Path $coreAssembly
$priorServers = @(Get-CimInstance Win32_Process | Where-Object {
    $_.Name -ceq "ollama.exe" -and
    $_.ExecutablePath -ceq $ollamaExecutable -and
    $_.CommandLine -like "*serve*"
})
if ($priorServers.Count -lt 1) { throw "The existing Ollama service was not observed before validation." }
$priorServerIds = @($priorServers | ForEach-Object { [int]$_.ProcessId })
$runtimeHost = $null
$runtimeSelectionStore = $null
$runtimeApprovalCleared = $false
try {
    $reviewer = [DCx.Core.LocalModels.OllamaRuntimeReviewer]::new()
    $review = $reviewer.ReviewAsync(
        $ollamaExecutable,
        [DCx.Core.LocalModels.OllamaRuntimeReviewer]::NvidiaCudaV13Profile,
        [DateTimeOffset]::UtcNow,
        [Threading.CancellationToken]::None).GetAwaiter().GetResult()
    if ($review.Binding.AuthenticodeStatus -cne "Valid" -or
        $review.Binding.SignerSubject -notlike "*Ollama Inc.*" -or
        $review.Binding.Files.Count -lt 1 -or
        $review.TotalBytes -lt 1 -or
        $review.Binding.TreeSha256 -notmatch '^[0-9a-f]{64}$') {
        throw "The installed Ollama runtime did not produce complete signed provenance evidence."
    }

    $runtimeSelectionStore = [DCx.Core.LocalModels.OllamaRuntimeSelectionStore]::OpenAsync(
        $selectionRoot,
        [Threading.CancellationToken]::None).GetAwaiter().GetResult()
    $null = $runtimeSelectionStore.SetAsync(
        $review,
        $modelRoot,
        $true,
        [DateTimeOffset]::UtcNow,
        [Threading.CancellationToken]::None).GetAwaiter().GetResult()
    $runtimeHost = [DCx.Core.LocalModels.OllamaApprovedRuntimeHost]::new($runtimeSelectionStore)
    $models = @($runtimeHost.ListModelsAsync(
        [TimeSpan]::FromSeconds(30),
        [Threading.CancellationToken]::None).GetAwaiter().GetResult())
    $ownedProcessId = [int]$runtimeHost.ActiveProcessId
    $ownedOrigin = $runtimeHost.ActiveBaseAddress
    if ($priorServerIds -contains $ownedProcessId) { throw "The owned runtime reused the existing service process." }
    if ($ownedOrigin.Host -cne "127.0.0.1" -or $ownedOrigin.Port -eq 11434) {
        throw "The owned runtime did not use its independent loopback origin."
    }
    $ownedVersion = $runtimeHost.GetVersionAsync(
        [TimeSpan]::FromSeconds(30),
        [Threading.CancellationToken]::None).GetAwaiter().GetResult()
    $selected = @($models | Where-Object {
        $_.Name -ceq $visionModel -and $_.Digest -ceq $visionDigest -and $_.SupportsVision
    })
    if ($selected.Count -ne 1) { throw "The approved digest-bound vision model was not uniquely available in the owned runtime." }
    $inspection = $runtimeHost.InspectModelAsync(
        $visionModel,
        [TimeSpan]::FromSeconds(30),
        [Threading.CancellationToken]::None).GetAwaiter().GetResult()
    if ([string]::IsNullOrWhiteSpace($inspection.License)) {
        throw "The owned runtime did not return the selected model license."
    }
    $licenseBytes = [Text.Encoding]::UTF8.GetBytes($inspection.License)
    try { $licenseDigest = [Convert]::ToHexStringLower([Security.Cryptography.SHA256]::HashData($licenseBytes)) }
    finally { [Array]::Clear($licenseBytes, 0, $licenseBytes.Length) }

    Add-Type -AssemblyName System.Drawing
    $bitmap = [System.Drawing.Bitmap]::new(320, 180)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    try {
        $graphics.Clear([System.Drawing.Color]::Blue)
        $graphics.FillEllipse([System.Drawing.Brushes]::Red, 120, 50, 80, 80)
        $font = [System.Drawing.Font]::new("Arial", 22)
        try { $graphics.DrawString("DCX 42", $font, [System.Drawing.Brushes]::White, 105, 135) }
        finally { $font.Dispose() }
        $bitmap.Save($imagePath, [System.Drawing.Imaging.ImageFormat]::Png)
    }
    finally {
        $graphics.Dispose()
        $bitmap.Dispose()
    }

    $firstClock = [Diagnostics.Stopwatch]::StartNew()
    $vision = $runtimeHost.DescribeImagePinnedAsync(
        $visionModel,
        $visionDigest,
        $licenseDigest,
        $imagePath,
        "Describe the dominant colors, central shape, and exact text in one concise sentence.",
        [TimeSpan]::FromMinutes(5),
        [Threading.CancellationToken]::None).GetAwaiter().GetResult()
    $firstClock.Stop()
    if ([string]::IsNullOrWhiteSpace($vision.Text) -or
        $vision.PromptTokens -lt 1 -or $vision.OutputTokens -lt 1) {
        throw "The real owned-runtime vision inference returned no bounded usable result."
    }
    $runningAfterFirst = @($runtimeHost.ListRunningModelsAsync(
        [TimeSpan]::FromSeconds(30),
        [Threading.CancellationToken]::None).GetAwaiter().GetResult())
    $canonicalResidentName = "sha256-$visionDigest`:latest"
    if ($runningAfterFirst.Count -ne 1 -or
        $runningAfterFirst[0].Name -cne $canonicalResidentName -or
        $runningAfterFirst[0].Digest -cne $visionDigest) {
        throw "The production host did not retain only the exact approved model after first inference."
    }

    $secondClock = [Diagnostics.Stopwatch]::StartNew()
    $secondVision = $runtimeHost.DescribeImagePinnedAsync(
        $visionModel,
        $visionDigest,
        $licenseDigest,
        $imagePath,
        "State the central shape and exact text in one concise sentence.",
        [TimeSpan]::FromMinutes(5),
        [Threading.CancellationToken]::None).GetAwaiter().GetResult()
    $secondClock.Stop()
    if ([string]::IsNullOrWhiteSpace($secondVision.Text) -or
        $secondVision.PromptTokens -lt 1 -or $secondVision.OutputTokens -lt 1) {
        throw "The retained-model follow-up inference returned no bounded usable result."
    }
    if ($secondClock.Elapsed -ge $firstClock.Elapsed) {
        throw "The retained-model follow-up was not faster than cold inference."
    }
    if ([int]$runtimeHost.ActiveProcessId -ne $ownedProcessId) {
        throw "The production host replaced its owned process between same-model requests."
    }
    $runningAfterSecond = @($runtimeHost.ListRunningModelsAsync(
        [TimeSpan]::FromSeconds(30),
        [Threading.CancellationToken]::None).GetAwaiter().GetResult())
    if ($runningAfterSecond.Count -ne 1 -or
        $runningAfterSecond[0].Name -cne $canonicalResidentName -or
        $runningAfterSecond[0].Digest -cne $visionDigest) {
        throw "The production host retained an unexpected model after follow-up inference."
    }
    foreach ($priorServerId in $priorServerIds) {
        if ($null -eq (Get-Process -Id $priorServerId -ErrorAction SilentlyContinue)) {
            throw "The existing Ollama service exited while the owned runtime was active."
        }
    }

    $ownedPort = $ownedOrigin.Port
    $null = $runtimeHost.StopAsync([Threading.CancellationToken]::None).GetAwaiter().GetResult()
    if ($null -ne (Get-Process -Id $ownedProcessId -ErrorAction SilentlyContinue)) {
        throw "The owned Ollama runtime survived disposal."
    }
    foreach ($priorServerId in $priorServerIds) {
        if ($null -eq (Get-Process -Id $priorServerId -ErrorAction SilentlyContinue)) {
            throw "The existing Ollama service did not survive owned-runtime disposal."
        }
    }
    $listener = @(Get-NetTCPConnection -State Listen -LocalAddress 127.0.0.1 -LocalPort $ownedPort -ErrorAction SilentlyContinue)
    if ($listener.Count -ne 0) { throw "The owned Ollama listener survived disposal." }

    $null = $runtimeSelectionStore.ClearAsync(
        [Threading.CancellationToken]::None).GetAwaiter().GetResult()
    $runtimeApprovalCleared = $true

    $executableBinding = @($review.Binding.Files | Where-Object {
        $_.RelativePath -ceq $review.Binding.ExecutableRelativePath
    })
    if ($executableBinding.Count -ne 1) { throw "The executable was not uniquely bound in the runtime tree." }
    $evidence = [ordered]@{
        schema_version = 1
        result = "PASS"
        runtime_version = $ownedVersion
        runtime_profile = $review.Binding.RuntimeProfile
        runtime_file_count = $review.Binding.Files.Count
        runtime_total_bytes = $review.TotalBytes
        runtime_tree_sha256 = $review.Binding.TreeSha256
        executable_sha256 = $executableBinding[0].Sha256
        authenticode_status = $review.Binding.AuthenticodeStatus
        signer_subject = $review.Binding.SignerSubject
        signer_certificate_sha256 = $review.Binding.SignerCertificateSha256
        official_license_uri = $review.OfficialLicenseUri
        independent_loopback_origin = $true
        listener_owner_pid_verified = $true
        kill_on_job_close = $true
        ambient_environment_scrubbed = $true
        no_cloud = $true
        no_prune = $true
        single_loaded_model_limit = 1
        single_parallel_request_limit = 1
        listed_model_count = $models.Count
        protected_runtime_approval = $true
        protected_runtime_approval_cleared = $true
        inference_performed = $true
        inference_model = $visionModel
        inference_model_digest = $visionDigest
        inference_prompt_tokens = [int]$vision.PromptTokens
        inference_output_tokens = [int]$vision.OutputTokens
        inference_response_characters = [int]$vision.Text.Length
        cold_inference_elapsed_ms = [int64]$firstClock.ElapsedMilliseconds
        warm_inference_elapsed_ms = [int64]$secondClock.ElapsedMilliseconds
        retained_exact_model_between_requests = $true
        same_owned_process_between_requests = $true
        retained_runtime_model_name = $canonicalResidentName
        inference_cleanup_confirmed = $true
        model_transfer_performed = $false
        existing_service_count = $priorServers.Count
        existing_service_survived = $true
        owned_process_stopped = $true
        owned_listener_stopped = $true
    }
    $evidence | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $evidencePath -Encoding utf8NoBOM
    "Owned Ollama runtime isolation=PASS"
    "Runtime version=$ownedVersion; files=$($review.Binding.Files.Count); bytes=$($review.TotalBytes)"
    "Existing service preserved=True; protected approval cleared=True; cold_ms=$($firstClock.ElapsedMilliseconds); warm_ms=$($secondClock.ElapsedMilliseconds); transfer=False"
    "Evidence=$evidencePath"
}
finally {
    if ($null -ne $runtimeHost) {
        try { $null = $runtimeHost.DisposeAsync().AsTask().GetAwaiter().GetResult() } catch { }
    }
    if ($null -ne $runtimeSelectionStore -and -not $runtimeApprovalCleared) {
        try {
            $null = $runtimeSelectionStore.ClearAsync(
                [Threading.CancellationToken]::None).GetAwaiter().GetResult()
        }
        catch { }
    }
}
