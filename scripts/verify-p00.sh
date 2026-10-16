#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
windows_repo="$(wslpath -w "$repo_root")"

cmd.exe /d /c ver
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "${windows_repo}\\scripts\\ci.ps1"

