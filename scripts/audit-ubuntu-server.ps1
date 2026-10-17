[CmdletBinding()]
param(
    [ValidatePattern('^[A-Za-z0-9._-]{1,128}$')]
    [string]$HostAlias = "ubuntu-server",
    [string]$RunRoot
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($RunRoot)) {
    $RunRoot = Join-Path $repoRoot "artifacts\test-results\ubuntu-audit-$([Guid]::NewGuid().ToString('N'))"
}
$runRoot = [IO.Path]::GetFullPath($RunRoot)
$allowedRoot = [IO.Path]::GetFullPath((Join-Path $repoRoot "artifacts\test-results"))
if (-not $runRoot.StartsWith($allowedRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw "The Ubuntu audit run root must be beneath the repository test-results directory."
}
$ssh = (Get-Command ssh.exe -CommandType Application -ErrorAction Stop | Select-Object -First 1).Source
New-Item -ItemType Directory -Path $runRoot -Force | Out-Null

$remoteCommand = @'
set -eu
. /etc/os-release
emit() { printf '%s=%s\n' "$1" "$2"; }
emit os_id "$ID"
emit os_version "$VERSION_ID"
emit architecture "$(uname -m)"
emit kernel "$(uname -r)"
emit user_uid "$(id -u)"
if id -nG | tr ' ' '\n' | grep -Fxq docker; then emit docker_group true; else emit docker_group false; fi
if command -v docker >/dev/null 2>&1; then
  emit docker_cli "$(docker --version | sed -E 's/^Docker version ([^,]+),.*$/\1/')"
  emit docker_compose "$(docker compose version --short 2>/dev/null || printf unavailable)"
  if docker info --format '{{.ServerVersion}}' >/dev/null 2>&1; then emit docker_daemon_accessible true; else emit docker_daemon_accessible false; fi
else
  emit docker_cli absent
  emit docker_compose absent
  emit docker_daemon_accessible false
fi
emit docker_service "$(systemctl is-active docker 2>/dev/null || true)"
if command -v dotnet >/dev/null 2>&1; then
  emit dotnet_sdk "$(dotnet --version 2>/dev/null || printf unavailable)"
  if dotnet --list-runtimes 2>/dev/null | grep -Eq '^Microsoft\.NETCore\.App 10\.'; then emit dotnet_10_runtime true; else emit dotnet_10_runtime false; fi
  if dotnet --list-runtimes 2>/dev/null | grep -Eq '^Microsoft\.AspNetCore\.App 10\.'; then emit aspnet_10_runtime true; else emit aspnet_10_runtime false; fi
else
  emit dotnet_sdk absent
  emit dotnet_10_runtime false
  emit aspnet_10_runtime false
fi
if command -v bwrap >/dev/null 2>&1; then
  emit bubblewrap "$(bwrap --version | awk '{print $2}')"
  if bwrap --unshare-all --die-with-parent --ro-bind / / -- /usr/bin/true >/dev/null 2>&1; then emit bubblewrap_smoke true; else emit bubblewrap_smoke false; fi
else
  emit bubblewrap absent
  emit bubblewrap_smoke false
fi
emit cgroup_filesystem "$(stat -fc %T /sys/fs/cgroup 2>/dev/null || printf unavailable)"
emit cgroup_controllers "$(cat /sys/fs/cgroup/cgroup.controllers 2>/dev/null | tr ' ' ',' || printf unavailable)"
cgroup_path="$(awk -F: '$1 == "0" { print $3 }' /proc/self/cgroup)"
if test -n "$cgroup_path" && test -w "/sys/fs/cgroup${cgroup_path}/cgroup.procs"; then emit delegated_cgroup_writable true; else emit delegated_cgroup_writable false; fi
if test -r /proc/sys/kernel/unprivileged_userns_clone; then emit unprivileged_userns "$(cat /proc/sys/kernel/unprivileged_userns_clone)"; else emit unprivileged_userns unavailable; fi
if test -r /proc/sys/kernel/seccomp/actions_avail; then emit seccomp_actions "$(tr ' ' ',' </proc/sys/kernel/seccomp/actions_avail)"; else emit seccomp_actions unavailable; fi
emit memory_kib "$(awk '/MemTotal/{print $2}' /proc/meminfo)"
emit root_free_kib "$(df -Pk / | awk 'NR == 2 { print $4 }')"
emit existing_dcx_directories "$(find "$HOME" -maxdepth 3 -type d -iname '*dcx*' -print 2>/dev/null | wc -l)"
'@

$startInfo = [Diagnostics.ProcessStartInfo]::new($ssh)
$startInfo.UseShellExecute = $false
$startInfo.CreateNoWindow = $true
$startInfo.RedirectStandardOutput = $true
$startInfo.RedirectStandardError = $true
foreach ($argument in @(
    "-o", "BatchMode=yes",
    "-o", "ConnectTimeout=10",
    "-o", "StrictHostKeyChecking=yes",
    $HostAlias,
    $remoteCommand)) {
    $null = $startInfo.ArgumentList.Add($argument)
}
$process = [Diagnostics.Process]::Start($startInfo)
if ($null -eq $process) { throw "The read-only Ubuntu audit process did not start." }
$stdoutTask = $process.StandardOutput.ReadToEndAsync()
$stderrTask = $process.StandardError.ReadToEndAsync()
try {
    $completed = $process.WaitForExitAsync([Threading.CancellationToken]::None).Wait([TimeSpan]::FromSeconds(30))
    if (-not $completed) {
        $process.Kill($true)
        $process.WaitForExit()
        throw "The read-only Ubuntu audit exceeded 30 seconds."
    }
    $stdout = $stdoutTask.GetAwaiter().GetResult()
    $stderr = $stderrTask.GetAwaiter().GetResult()
    $exitCode = $process.ExitCode
}
finally { $process.Dispose() }
if ($stdout.Length -gt 32768 -or $stderr.Length -gt 8192) {
    throw "The read-only Ubuntu audit output exceeded its limit."
}
if ($exitCode -ne 0) {
    throw "The read-only Ubuntu audit failed without retaining remote diagnostics."
}

$values = @{}
foreach ($line in @($stdout -split "`r?`n" | Where-Object { $_.Length -gt 0 })) {
    if ($line -notmatch '^([a-z0-9_]{1,64})=([ -~]{0,256})$') {
        throw "The read-only Ubuntu audit returned malformed output."
    }
    if ($values.ContainsKey($Matches[1])) { throw "The read-only Ubuntu audit returned a duplicate field." }
    $values[$Matches[1]] = $Matches[2]
}
$required = @(
    "os_id", "os_version", "architecture", "kernel", "user_uid", "docker_group",
    "docker_cli", "docker_compose", "docker_daemon_accessible", "docker_service", "dotnet_sdk",
    "dotnet_10_runtime", "aspnet_10_runtime", "bubblewrap", "bubblewrap_smoke",
    "cgroup_filesystem", "cgroup_controllers", "delegated_cgroup_writable", "unprivileged_userns",
    "seccomp_actions", "memory_kib", "root_free_kib", "existing_dcx_directories")
if (@($required | Where-Object { -not $values.ContainsKey($_) }).Count -ne 0) {
    throw "The read-only Ubuntu audit omitted required fields."
}
if ($values.os_id -ne "ubuntu" -or $values.os_version -ne "24.04" -or $values.architecture -ne "x86_64" -or
    $values.user_uid -eq "0" -or $values.dotnet_10_runtime -ne "true" -or
    $values.aspnet_10_runtime -ne "true" -or
    $values.cgroup_filesystem -ne "cgroup2fs") {
    throw "The Ubuntu server does not satisfy the nonprivileged baseline."
}

$evidence = [ordered]@{
    captured_at_utc = (Get-Date).ToUniversalTime().ToString("O")
    ssh_host_alias = $HostAlias
    audit_mode = "read_only_nonprivileged"
    os_id = $values.os_id
    os_version = $values.os_version
    architecture = $values.architecture
    kernel = $values.kernel
    user_uid = [int]$values.user_uid
    docker_cli = $values.docker_cli
    docker_compose = $values.docker_compose
    docker_service = $values.docker_service
    docker_group = [bool]::Parse($values.docker_group)
    docker_daemon_accessible = [bool]::Parse($values.docker_daemon_accessible)
    dotnet_sdk = $values.dotnet_sdk
    dotnet_10_runtime = [bool]::Parse($values.dotnet_10_runtime)
    aspnet_10_runtime = [bool]::Parse($values.aspnet_10_runtime)
    bubblewrap = $values.bubblewrap
    bubblewrap_smoke = [bool]::Parse($values.bubblewrap_smoke)
    cgroup_filesystem = $values.cgroup_filesystem
    cgroup_controllers = @($values.cgroup_controllers -split ',' | Where-Object { $_.Length -gt 0 })
    delegated_cgroup_writable = [bool]::Parse($values.delegated_cgroup_writable)
    unprivileged_userns = $values.unprivileged_userns
    seccomp_actions = @($values.seccomp_actions -split ',' | Where-Object { $_.Length -gt 0 })
    memory_kib = [long]$values.memory_kib
    root_free_kib = [long]$values.root_free_kib
    existing_dcx_directories = [int]$values.existing_dcx_directories
    files_written_remote = 0
    packages_installed = 0
    services_changed = 0
    containers_started = 0
    deployment_attempted = $false
}
$evidencePath = Join-Path $runRoot "ubuntu-server-audit.json"
$evidence | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $evidencePath -Encoding utf8

"Ubuntu server SSH host-key and key-only connection=PASS"
"Ubuntu=$($evidence.os_version); architecture=$($evidence.architecture); UID=$($evidence.user_uid)"
".NET 10 runtime=$($evidence.dotnet_10_runtime); ASP.NET 10 runtime=$($evidence.aspnet_10_runtime)"
"Bubblewrap $($evidence.bubblewrap) network-isolated nonprivileged smoke=$($evidence.bubblewrap_smoke)"
"Docker $($evidence.docker_cli); Compose $($evidence.docker_compose); service=$($evidence.docker_service); daemon accessible=$($evidence.docker_daemon_accessible)"
"cgroup v2=$($evidence.cgroup_filesystem -eq 'cgroup2fs'); delegated writable=$($evidence.delegated_cgroup_writable)"
"Remote writes=0 installs=0 service changes=0 containers=0 deployments=0"
"Ubuntu audit evidence=$evidencePath"
