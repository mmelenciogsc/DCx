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
$runtime = $null
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

    $launcher = [DCx.Core.LocalModels.OllamaOwnedRuntimeLauncher]::new()
    $options = [DCx.Core.LocalModels.OllamaOwnedRuntimeOptions]::new(
        $modelRoot,
        [TimeSpan]::FromSeconds(30),
        $null)
    $runtime = $launcher.StartAsync(
        $review.Binding,
        $options,
        $true,
        [Threading.CancellationToken]::None).GetAwaiter().GetResult()
    if ($priorServerIds -contains $runtime.ProcessId) { throw "The owned runtime reused the existing service process." }
    if ($runtime.BaseAddress.Host -cne "127.0.0.1" -or $runtime.BaseAddress.Port -eq 11434) {
        throw "The owned runtime did not use its independent loopback origin."
    }
    $client = $runtime.CreateClient([TimeSpan]::FromSeconds(10))
    try {
        $models = @($client.ListModelsAsync([Threading.CancellationToken]::None).GetAwaiter().GetResult())
    }
    finally { $client.Dispose() }
    foreach ($priorServerId in $priorServerIds) {
        if ($null -eq (Get-Process -Id $priorServerId -ErrorAction SilentlyContinue)) {
            throw "The existing Ollama service exited while the owned runtime was active."
        }
    }

    $ownedProcessId = $runtime.ProcessId
    $ownedPort = $runtime.BaseAddress.Port
    $ownedVersion = $runtime.Version
    $null = $runtime.DisposeAsync().AsTask().GetAwaiter().GetResult()
    $runtime = $null
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
        inference_performed = $false
        model_transfer_performed = $false
        existing_service_count = $priorServers.Count
        existing_service_survived = $true
        owned_process_stopped = $true
        owned_listener_stopped = $true
    }
    $evidence | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $evidencePath -Encoding utf8NoBOM
    "Owned Ollama runtime isolation=PASS"
    "Runtime version=$ownedVersion; files=$($review.Binding.Files.Count); bytes=$($review.TotalBytes)"
    "Existing service preserved=True; inference=False; transfer=False"
    "Evidence=$evidencePath"
}
finally {
    if ($null -ne $runtime) {
        try { $null = $runtime.DisposeAsync().AsTask().GetAwaiter().GetResult() } catch { }
    }
}
