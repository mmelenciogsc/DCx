#!/usr/bin/env bash
set -euo pipefail
umask 077

repo_root=$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd -P)
run_root=${1:-$repo_root/artifacts/test-results/p10-sandbox-$(date -u +%Y%m%dT%H%M%SZ)}
launcher=$repo_root/server/sandbox/run-ccb-bubblewrap.sh
bwrap_path=/usr/bin/bwrap
runtime_path=/usr/bin/busybox

[[ -x $launcher && -x $bwrap_path && -x $runtime_path ]] || {
    printf 'P10 sandbox prerequisites are unavailable.\n' >&2
    exit 1
}

bwrap_sha256=$(sha256sum "$bwrap_path" | awk '{ print $1 }')
runtime_sha256=$(sha256sum "$runtime_path" | awk '{ print $1 }')
launcher_sha256=$(sha256sum "$launcher" | awk '{ print $1 }')
quarantine_root=$(mktemp -d /tmp/dcx-p10-quarantine.XXXXXX)
workspace=$quarantine_root/package
input_path=$quarantine_root/request.bin
outside_workspace=
cleanup() {
    rm -rf -- "$quarantine_root"
    if [[ -n $outside_workspace ]]; then rm -rf -- "$outside_workspace"; fi
}
trap cleanup EXIT
mkdir -p "$workspace"
cp "$repo_root/tests/fixtures/ccb-sandbox/"*.sh "$workspace/"
printf 'dcx sandbox 42\n' >"$input_path"

workspace_sha256() {
    (
        cd "$1"
        find . -xdev -type f -print0 | LC_ALL=C sort -z | xargs -0 sha256sum -- | sha256sum | awk '{ print $1 }'
    )
}

run_sandbox() {
    local digest
    digest=$(workspace_sha256 "$workspace")
    "$launcher" \
        "$quarantine_root" \
        "$workspace" \
        "$digest" \
        "$1" \
        "$input_path" \
        "$bwrap_path" \
        "$bwrap_sha256" \
        "$runtime_path" \
        "$runtime_sha256"
}

good_output=$(run_sandbox good.sh)
[[ $good_output == 'DCX SANDBOX 42' ]]

set +e
"$launcher" "$quarantine_root" "$workspace" "$(printf mismatch | sha256sum | awk '{ print $1 }')" good.sh "$input_path" "$bwrap_path" "$bwrap_sha256" "$runtime_path" "$runtime_sha256" >/dev/null 2>&1
workspace_digest_status=$?
set -e
[[ $workspace_digest_status -eq 65 ]]

export DCX_TEST_SECRET=must-not-cross-boundary
probe_output=$(run_sandbox hostile-probe.sh)
expected_probe='host_files=blocked system_files=blocked network=blocked root_write=blocked environment=cleared uid=65534 gid=65534 cpu=5 memory=262144 processes=32 files=64 output=2048'
[[ $probe_output == "$expected_probe" ]]
unset DCX_TEST_SECRET

outside_workspace=$(mktemp -d)
cp "$repo_root/tests/fixtures/ccb-sandbox/good.sh" "$outside_workspace/good.sh"
outside_digest=$(workspace_sha256 "$outside_workspace")
set +e
"$launcher" "$quarantine_root" "$outside_workspace" "$outside_digest" good.sh "$input_path" "$bwrap_path" "$bwrap_sha256" "$runtime_path" "$runtime_sha256" >/dev/null 2>&1
outside_status=$?
set -e
[[ $outside_status -eq 65 ]]

ln -s /etc/passwd "$workspace/host-link"
set +e
run_sandbox good.sh >/dev/null 2>&1
symlink_status=$?
set -e
rm "$workspace/host-link"
[[ $symlink_status -eq 65 ]]

set +e
run_sandbox timeout.sh >/dev/null 2>&1
timeout_status=$?
set -e
[[ $timeout_status -eq 70 ]]

set +e
run_sandbox oversize-output.sh >/dev/null 2>&1
output_status=$?
set -e
[[ $output_status -eq 71 ]]

mkdir -p "$run_root"
evidence_path=$run_root/sandbox-evidence.json
printf '{\n  "policy_version": "dcx-ccb-bwrap-v1",\n  "bubblewrap_version": "%s",\n  "bubblewrap_sha256": "%s",\n  "runtime_sha256": "%s",\n  "launcher_sha256": "%s",\n  "good_fixture": true,\n  "workspace_digest_enforced": true,\n  "host_files_hidden": true,\n  "system_files_hidden": true,\n  "network_denied": true,\n  "root_write_denied": true,\n  "environment_cleared": true,\n  "non_root_uid": 65534,\n  "outside_workspace_denied": true,\n  "symlink_denied": true,\n  "timeout_enforced": true,\n  "output_limit_enforced": true\n}\n' \
    "$(bwrap --version | tr -d '\r\n')" \
    "$bwrap_sha256" \
    "$runtime_sha256" \
    "$launcher_sha256" \
    >"$evidence_path"

printf 'P10 Bubblewrap policy=dcx-ccb-bwrap-v1\n'
printf 'Bubblewrap=%s; SHA-256=%s\n' "$(bwrap --version)" "$bwrap_sha256"
printf 'Static runtime SHA-256=%s\n' "$runtime_sha256"
printf 'Read-only package and request mounts=PASS\n'
printf 'Canonical workspace digest binding=PASS\n'
printf 'Host/system filesystem invisibility=PASS\n'
printf 'Network namespace denial=PASS\n'
printf 'Cleared environment and non-root UID=PASS\n'
printf 'Outside-workspace and symlink denial=PASS\n'
printf 'CPU/memory/process/file/output bounds=PASS\n'
printf 'Wall timeout and output overflow denial=PASS\n'
printf 'P10 hostile CCB sandbox validation=PASS\n'
printf 'P10 sandbox evidence=%s\n' "$evidence_path"
