#!/usr/bin/env bash
set -euo pipefail

# Installs the repository-pinned .NET SDK into a user-local directory only.
# It deliberately does not alter apt sources, system SDKs, Docker, or PATH.

readonly sdk_version='10.0.401'
readonly sdk_url='https://builds.dotnet.microsoft.com/dotnet/Sdk/10.0.401/dotnet-sdk-10.0.401-linux-x64.tar.gz'
readonly sdk_sha512='51c8b999af9e8dd9998c9edc5944e19a90788862068acd38694e098889054ce8c23d4f0c5cccfa16bf187d044562359e5ee69a9f8ad0bbe913ba90311fbce25b'
sdk_root="${DCX_DOTNET_ROOT:-/home/marx/.local/share/dotnet-${sdk_version}}"
verify_only=false

if [[ "${1:-}" == '--verify-only' ]]; then
  verify_only=true
elif [[ $# -ne 0 ]]; then
  printf 'Usage: %s [--verify-only]\n' "$0" >&2
  exit 64
fi

verify_sdk() {
  [[ -x "$sdk_root/dotnet" ]] || return 1
  [[ "$(DOTNET_GENERATE_ASPNET_CERTIFICATE=false "$sdk_root/dotnet" --version)" == "$sdk_version" ]]
}

if verify_sdk; then
  printf 'Verified .NET SDK %s at %s\n' "$sdk_version" "$sdk_root"
  exit 0
fi

if [[ "$verify_only" == true ]]; then
  printf 'Expected .NET SDK %s at %s was not found.\n' "$sdk_version" "$sdk_root" >&2
  exit 69
fi

if [[ -e "$sdk_root" ]]; then
  printf 'Refusing to overwrite existing SDK target: %s\n' "$sdk_root" >&2
  exit 73
fi

command -v curl >/dev/null
command -v sha512sum >/dev/null
command -v tar >/dev/null

parent_dir="$(dirname "$sdk_root")"
mkdir -p "$parent_dir"
stage_dir="$(mktemp -d "${parent_dir}/.dcx-dotnet-stage.XXXXXX")"
archive_path="$stage_dir/dotnet-sdk-${sdk_version}-linux-x64.tar.gz"
trap 'rm -rf "$stage_dir"' EXIT

curl --fail --location --proto '=https' --tlsv1.2 --output "$archive_path" "$sdk_url"
printf '%s  %s\n' "$sdk_sha512" "$archive_path" | sha512sum --check --status
tar -xzf "$archive_path" -C "$stage_dir"

if [[ ! -x "$stage_dir/dotnet" ]] || [[ "$(DOTNET_GENERATE_ASPNET_CERTIFICATE=false "$stage_dir/dotnet" --version)" != "$sdk_version" ]]; then
  printf 'Downloaded SDK did not report expected version %s.\n' "$sdk_version" >&2
  exit 65
fi

rm -f "$archive_path"
mv "$stage_dir" "$sdk_root"
trap - EXIT
printf 'Installed verified .NET SDK %s at %s\n' "$sdk_version" "$sdk_root"
