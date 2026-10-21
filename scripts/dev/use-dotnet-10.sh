#!/usr/bin/env bash
set -euo pipefail

# This wrapper intentionally never downloads, installs, or changes a system SDK.
# Provision the exact SDK through the documented checksum-verified U16 process,
# then override DCX_DOTNET_ROOT when the user-local location differs.
sdk_root="${DCX_DOTNET_ROOT:-/home/marx/.local/share/dotnet-10.0.401}"
dotnet_bin="$sdk_root/dotnet"

# Prevent a CLI first-run side effect before even the version gate executes.
export DOTNET_CLI_TELEMETRY_OPTOUT=1
export DOTNET_GENERATE_ASPNET_CERTIFICATE=false
export DOTNET_NOLOGO=1

if [[ ! -x "$dotnet_bin" ]]; then
  printf 'DCx requires SDK 10.0.401 at %s; see docs/UBUNTU_WINDOWS_HANDOFF.md.\n' "$sdk_root" >&2
  exit 69
fi

actual_version="$($dotnet_bin --version)"
if [[ "$actual_version" != '10.0.401' ]]; then
  printf 'DCx requires SDK 10.0.401, but %s reports %s.\n' "$dotnet_bin" "$actual_version" >&2
  exit 69
fi

export DOTNET_ROOT="$sdk_root"
exec "$dotnet_bin" "$@"
