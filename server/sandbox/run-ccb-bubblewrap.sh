#!/usr/bin/env bash
set -euo pipefail
umask 077

if [[ $# -ne 9 ]]; then
    exit 64
fi

quarantine_root=$1
workspace=$2
workspace_sha256=$3
entrypoint=$4
input_path=$5
bwrap_path=$6
bwrap_sha256=$7
runtime_path=$8
runtime_sha256=$9

fail_admission() {
    exit 65
}

is_sha256() {
    [[ $1 =~ ^[0-9A-Fa-f]{64}$ ]]
}

[[ -d $quarantine_root && -d $workspace && -f $input_path ]] || fail_admission
[[ -f $bwrap_path && -x $bwrap_path && -f $runtime_path && -x $runtime_path ]] || fail_admission
is_sha256 "$workspace_sha256" && is_sha256 "$bwrap_sha256" && is_sha256 "$runtime_sha256" || fail_admission

quarantine_root=$(realpath -e -- "$quarantine_root")
workspace=$(realpath -e -- "$workspace")
input_path=$(realpath -e -- "$input_path")
bwrap_path=$(realpath -e -- "$bwrap_path")
runtime_path=$(realpath -e -- "$runtime_path")

case "$workspace/" in "$quarantine_root/"*) ;; *) fail_admission ;; esac
case "$input_path" in "$quarantine_root/"*) ;; *) fail_admission ;; esac
[[ $workspace != "$quarantine_root" ]] || fail_admission
[[ $entrypoint =~ ^[A-Za-z0-9._/-]+\.sh$ ]] || fail_admission
[[ $entrypoint != /* && $entrypoint != *"//"* && $entrypoint != ".." && $entrypoint != ../* && $entrypoint != */../* && $entrypoint != */.. ]] || fail_admission

entrypoint_path=$(realpath -e -- "$workspace/$entrypoint")
case "$entrypoint_path" in "$workspace/"*) ;; *) fail_admission ;; esac
[[ -f $entrypoint_path && ! -L $entrypoint_path ]] || fail_admission

if find "$workspace" -xdev \( -type l -o \! -type d -a \! -type f \) -print -quit | grep -q .; then
    fail_admission
fi
if find "$workspace" -xdev -mindepth 1 -printf '%P\n' | grep -Ev '^[A-Za-z0-9._/-]+$' | grep -q .; then
    fail_admission
fi

entry_count=$(find "$workspace" -xdev -mindepth 1 -printf '.' | wc -c)
expanded_bytes=$(find "$workspace" -xdev -type f -printf '%s\n' | awk '{ total += $1 } END { print total + 0 }')
input_bytes=$(stat -c '%s' -- "$input_path")
[[ $entry_count -le 256 && $expanded_bytes -le 67108864 && $input_bytes -le 1048576 ]] || fail_admission

actual_workspace_sha256=$(
    cd "$workspace"
    find . -xdev -type f -print0 | LC_ALL=C sort -z | xargs -0 sha256sum -- | sha256sum | awk '{ print $1 }'
)
[[ ${actual_workspace_sha256,,} == ${workspace_sha256,,} ]] || fail_admission

actual_bwrap_sha256=$(sha256sum -- "$bwrap_path" | awk '{ print $1 }')
actual_runtime_sha256=$(sha256sum -- "$runtime_path" | awk '{ print $1 }')
[[ ${actual_bwrap_sha256,,} == ${bwrap_sha256,,} && ${actual_runtime_sha256,,} == ${runtime_sha256,,} ]] || fail_admission

for trusted_path in "$bwrap_path" "$runtime_path"; do
    permissions=$(stat -c '%A' -- "$trusted_path")
    [[ ${permissions:5:1} != w && ${permissions:8:1} != w ]] || fail_admission
done

operation_root=$(mktemp -d "${TMPDIR:-/tmp}/dcx-ccb-sandbox.XXXXXX")
trap 'rm -rf -- "$operation_root"' EXIT
stdout_path=$operation_root/stdout.bin
stderr_path=$operation_root/stderr.bin

set +e
/usr/bin/timeout --signal=KILL 8s \
    "$bwrap_path" \
    --unshare-all \
    --die-with-parent \
    --new-session \
    --hostname dcx-ccb \
    --cap-drop ALL \
    --clearenv \
    --setenv PATH /runtime \
    --setenv HOME /nonexistent \
    --setenv LANG C \
    --ro-bind "$runtime_path" /runtime/busybox \
    --ro-bind "$workspace" /package \
    --ro-bind "$input_path" /input/request \
    --proc /proc \
    --dev /dev \
    --tmpfs /tmp \
    --tmpfs /work \
    --remount-ro / \
    --chdir /work \
    --uid 65534 \
    --gid 65534 \
    /runtime/busybox sh -c \
    'ulimit -t 5; ulimit -v 262144; ulimit -p 32; ulimit -n 64; ulimit -f 2048; exec /runtime/busybox sh "$1"' \
    dcx-sandbox "/package/$entrypoint" \
    >"$stdout_path" 2>"$stderr_path"
status=$?
set -e

stdout_bytes=$(stat -c '%s' -- "$stdout_path")
stderr_bytes=$(stat -c '%s' -- "$stderr_path")
if [[ $status -eq 124 || $status -eq 137 ]]; then
    exit 70
fi
if [[ $stdout_bytes -ge 1048576 || $stderr_bytes -ge 65536 || $status -eq 153 ]]; then
    exit 71
fi
if [[ $status -ne 0 ]]; then
    exit 72
fi

/bin/cat -- "$stdout_path"
