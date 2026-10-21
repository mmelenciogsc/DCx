#!/usr/bin/env bash
set -euo pipefail

# Linux evidence only: this avoids Windows-only credential, path, executable,
# foreground-app, installer, and assistive-technology tests. It proves neither
# Windows release readiness nor a replacement for scripts/ci.ps1.

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd -P)"
runner="$repo_root/scripts/dev/use-dotnet-10.sh"
tests_project='tests/DCx.Core.Tests/DCx.Core.Tests.csproj'
portable_filter='FullyQualifiedName~DCx.Core.Tests.OpenAI.OpenAiAccessPolicyTests|FullyQualifiedName~DCx.Core.Tests.OpenAI.SecretRedactorTests'

cd "$repo_root"
"$repo_root/scripts/dev/provision-dotnet-10.sh" --verify-only
"$runner" restore "$tests_project" --locked-mode
"$runner" build "$tests_project" --configuration Release --no-restore
"$runner" test "$tests_project" --configuration Release --no-build --no-restore --filter "$portable_filter"
