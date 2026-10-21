# Ubuntu–Windows development handoff

Updated: 2026-10-22

## Scope and authority

This record establishes a development handoff only. DCx remains an installable Windows 11+ product under development. The Ubuntu server is a development and future global CKB/CCB/accounting distribution surface; it is not a customer-agent execution platform and must not receive customer prompts, browser credentials, private CKB/CCB content, or routine user files.

The Windows laptop is the only target for Windows-native build, WebView2, installer, foreground-app, device, and human assistive-technology validation. An SSH command can prove a machine-observable command result, but cannot prove a logged-in desktop, focus, MFA, consent, media capture, or JAWS/NVDA/Narrator usability.

## Reconciled handoff baseline

| Check | Observed result |
|---|---|
| GitHub remote | `https://github.com/mmelenciogsc/DCx` |
| Baseline SHA | `d5e4477319414830017420673e5c3b0fee510ebd` (`docs: add continuation state handoff`) |
| Git reconciliation | Ubuntu source checkout, fresh Ubuntu clone, Windows `HEAD`, Windows `origin/main`, and GitHub `refs/heads/main` matched the baseline SHA. |
| Windows working tree | `git status --porcelain` returned zero entries. |
| Former Codex session | Zero `codex` processes were observed through a read-only SSH probe. This is evidence that no process was active at probe time, not proof about any GUI session. |
| Preserved validation | The inherited record reports 701/701 native Windows tests, zero warnings/errors, at source `7ff6387`; P13 60/60, package-trust 22/22, and WebView2-policy 3/3. These are historical Windows results, not rerun by this Ubuntu handoff. |
| Validation/source relation | `7ff6387` is an ancestor of the baseline. The intervening changes are continuation/security/accessibility/progress documentation and the current-state record; inspect the commit range before relying on an older test result for a changed source path. |
| Ubuntu feature branch | `codex/ubuntu-ui-continuation-u00` in `/home/marx/dcx-ubuntu-ui-20261022`, created from the baseline without modifying `main`. |

The Windows repository is on a filesystem Git treats as ownership-ambiguous for the SSH account. Read-only checks used a command-scoped `-c safe.directory=D:/DCx`; no global Git setting was changed.

## Ubuntu preflight

| Item | Observed result | Consequence |
|---|---|---|
| OS-side toolchain | Git 2.43.0, Python 3.12.3, Node 22.22.3, Docker 29.5.2 / Compose 5.1.4 | Available for audit and later bounded service tests. |
| DCx SDK requirement | `global.json` pins .NET SDK 10.0.401 | Required for DCx build/test. |
| System `dotnet` | 8.0.132 remains installed system-wide. | It was not altered. DCx does not use it in the Ubuntu validation path. |
| DCx SDK 10.0.401 | Provisioned user-locally at `/home/marx/.local/share/dotnet-10.0.401`; `dotnet --version` returned `10.0.401`. | The verified archive was Microsoft’s Linux x64 SDK URL below, SHA-512 verified before extraction. No apt source, system SDK, Docker setting, service, or global PATH was changed. |
| First-run certificate side effect | An initial SDK invocation created an untrusted `CN=localhost` development certificate. The exact newly-created certificate (`2491CDB98403907916384B46E051F2ADAD80DC24`) was then removed; the pre-existing certificate was preserved. | No certificate was trusted, exported, used by DCx, or deployed. The repository wrappers set `DOTNET_GENERATE_ASPNET_CERTIFICATE=false` for subsequent commands. |
| Storage | Root filesystem had 38 GiB free; `/home/marx/driveB` had about 2.2 GiB free | The isolated clone resides outside the nearly full DriveB filesystem. Do not place model caches, Docker volumes, or generated artifacts in DriveB. |
| Local agent lanes | BOSS health check reported both Senior and Junior lanes healthy and GPU-pinned | Available for bounded local review/coding packets, not as a substitute for Windows validation. |
| Source license | Proprietary/all rights reserved; third-party notices are incomplete until P13 SBOM review | Do not add, download, or redistribute a dependency without license/version/SBOM review. |
| Credential exposure check | No tracked credential-named file was found. A name-only keyword scan returned expected documentation, test, security, and adapter sources. | This is not a secret-certification scan; do not commit credentials, browser profiles, model caches, databases, or runtime volumes. |

## Cross-host operating rules

1. Ubuntu owns this feature branch. The Windows checkout remains read-only until a specific native validation command is selected and logged.
2. Fetch and compare SHA before every Windows validation. Never reset, clean, force-push, or use an unreviewed merge to make the worktrees match.
3. Exchange source through reviewed Git commits or explicitly approved checksum-bound artifacts. Do not SCP browser profiles, credentials, private indexes, user data, or model weights.
4. Ubuntu may validate portable policy, source, unit/API/container simulation, security, and documentation gates after its SDK gap is resolved. Windows must validate .NET/WPF/WebView2/installer/host-bridge code.
5. Privileged host actions, driver changes, WSL/Docker configuration, model downloads, device forwarding, account login, MFA, payments, external messages, publication, merge to a protected branch, and production deployment require separate explicit approval.

## Reproducible user-local SDK 10 provision

The pinned SDK version is from [`global.json`](../global.json). The provisioner’s pinned URL and digest were verified against Microsoft’s [official .NET 10 release metadata](https://builds.dotnet.microsoft.com/dotnet/release-metadata/10.0/releases.json) and it downloads only this archive:

```text
https://builds.dotnet.microsoft.com/dotnet/Sdk/10.0.401/dotnet-sdk-10.0.401-linux-x64.tar.gz
SHA-512 51c8b999af9e8dd9998c9edc5944e19a90788862068acd38694e098889054ce8c23d4f0c5cccfa16bf187d044562359e5ee69a9f8ad0bbe913ba90311fbce25b
```

`scripts/dev/provision-dotnet-10.sh` downloads over HTTPS, verifies that digest before extraction, validates `dotnet --version`, and refuses to overwrite a nonmatching destination. It installs to `/home/marx/.local/share/dotnet-10.0.401` by default; use `DCX_DOTNET_ROOT` only to select another user-owned destination. It never invokes root, apt, a service manager, Docker, or a global PATH edit.

```bash
cd /home/marx/dcx-ubuntu-ui-20261022
scripts/dev/provision-dotnet-10.sh
scripts/dev/provision-dotnet-10.sh --verify-only
scripts/dev/validate-ubuntu-portable.sh
```

The portable script runs locked restore, Release build, and three source-pure OpenAI access/redaction tests. It intentionally does **not** substitute for the native Windows suite: Windows backslash traversal semantics, `.exe` fake-process fixtures, Windows Credential Manager, installer/WebView2, foreground desktop, hardware, and screen-reader requirements retain Windows/manual gates.

## Safe reproducible checks

Run these only from the named worktree and retain stdout/stderr as evidence. The Windows command is read-only until its target script itself is approved for execution.

```bash
# Ubuntu: reconcile without changing the working tree.
git -C /home/marx/dcx-ubuntu-ui-20261022 fetch --prune origin
git -C /home/marx/dcx-ubuntu-ui-20261022 status --short --branch
git -C /home/marx/dcx-ubuntu-ui-20261022 rev-parse HEAD origin/main

# Windows: fixed read-only repository reconciliation through the configured alias.
ssh -F /home/marx/.ssh/config -i /home/marx/.ssh/id_ed25519_codex_to_windows \
  -o IdentitiesOnly=yes -o UserKnownHostsFile=/home/marx/.ssh/known_hosts \
  -o BatchMode=yes -o StrictHostKeyChecking=yes win11-laptop \
  'powershell.exe -NoProfile -Command "Set-Location D:\\DCx; git -c safe.directory=D:/DCx status --short --branch; git -c safe.directory=D:/DCx rev-parse HEAD \"@{u}\""'
```

When U16 has an approved Windows validation packet, use an existing repository validator such as `scripts/ci.ps1` or the relevant `validate-pXX.ps1`, bind it to the selected SHA, and capture its artifact path/checksum. It does not establish manual accessibility acceptance.

## Current blockers

- No Docker-managed Debian UI/backend/agentic continuation architecture or typed Windows–Debian bridge has been implemented or proven.
- No common live WebView2 page/session is yet demonstrated to both user and agent under the new Docker-backed design.
- Full U16 cross-host evidence has not been rerun: no checksum-bound Windows build/test result has been captured for this branch, and no JAWS/NVDA/Narrator, installer, device, GPU passthrough, login/MFA, camera/microphone, Bluetooth, or removable-storage manual acceptance was performed.
