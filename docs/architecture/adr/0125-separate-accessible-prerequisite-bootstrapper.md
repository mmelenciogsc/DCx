# ADR 0125: Separate the accessible prerequisite bootstrapper from DCx MSIX

- Status: Accepted for source-only bootstrapper construction; release and live installation remain blocked.
- Date: 2026-10-27

## Context

DCx's current MSIX is unsigned, synthetic-identity local-validation output.  It must not
quietly acquire or execute third-party installers, accept vendor or source agreements,
enable Windows features, restart a device, or cause Docker Desktop to become a hidden
requirement for ordinary product startup.

The Windows validation host has .NET, the Windows SDK, and Winget, but no WiX toolset.
Winget's `install` command supports silent and agreement-acceptance switches, so it is
not an acceptable v1 user-consent boundary.  Docker Desktop licensing/eligibility and
vendor installer behavior remain vendor-owned.

## Decision

Build a separately released, unpackaged WPF prerequisite-bootstrapper executable.  It
is not part of the unsigned MSIX, does not activate DCx, and is not automatically run at
DCx startup.  It reuses the reviewed Windows prerequisite catalog only for identity,
official-resource, publisher, and terms metadata; the existing Core policy remains a
no-install, exact official-resource handoff policy.

For each item, the bootstrapper must freshly detect status, display the exact terms and
official-resource addresses, and require two default-off, item-bound acknowledgements:

1. the user reviewed the vendor/Microsoft terms; and
2. the user asks to start the exact visible vendor/Microsoft installation flow.

WebView2 and Docker Desktop may be launched only from a previously downloaded,
user-selected artifact which passes a versioned approved-artifact policy (exact hash,
architecture, publisher/product identity, and path confinement).  The app does not
download that artifact.  WSL 2 may use only a fixed visible Microsoft command,
`wsl.exe --install --no-distribution`, with no caller-supplied arguments.  No flow uses
silent, quiet, agreement-acceptance, package-manager, PowerShell, automatic-reboot, or
post-install auto-start arguments.

The bootstrapper does not click UAC, vendor dialogs, terms checkboxes, or wizard buttons.
It records a launch as pending only; the user explicitly requests fresh detection after
closing the visible vendor/Microsoft flow or after any required reboot.  It never infers
successful installation from process exit and has no automatic retry, uninstall,
rollback, credential capture, or DCx activation behavior.

## Consequences

- The primary UI is native WPF with real UI Automation names, logical keyboard order,
  visible focus, high-contrast system colors, assertive status, and fixture-only native
  UIA coverage.
- A first release still needs an approved signing identity, separate artifact-catalog
  governance, SBOM/license review, Defender/SmartScreen review, clean Windows 11 VM
  evidence, and human Narrator/NVDA/JAWS tests across the bootstrapper and vendor-owned
  dialogs.
- Docker Desktop and WSL remain optional setup choices until their individual consent,
  visible vendor/Microsoft interaction, and post-install detection complete.  They are
  not silently imposed on unrelated DCx users.
