# ADR 0029: inert exact-package installation review before any deployment authority

- Status: Accepted for local validation; real installation remains blocked
- Date: 2026-10-17

## Context

P12 requires a package/install command approval scenario, while P13 still lacks an authorized MSIX toolchain, signing identity, production package verifier, clean Windows 11 VM and install/update/uninstall evidence. Connecting an approval button directly to PowerShell, `msiexec`, App Installer or a deployment API would create unvalidated installation authority and could turn a synthetic fixture into an OS side effect.

The existing controlled-update boundary stages inert bytes only. The existing process broker has no install operation. Those properties must remain true while DCx validates how an exact future request will be presented and approved accessibly.

## Decision

Add a separate ten-minute package-review session for a local `.msix` file. It rejects unsupported extensions, UNC/alternate-data-stream paths, missing/empty/oversized files and reparse-point files or ancestors. It holds the exact file under a read-only/no-write/no-delete share, records length and SHA-256, obtains injected package signature/identity/publisher/version evidence, and derives a canonical proposal digest.

The preview is a typed operation, not a shell string: `Windows.Management.Deployment.PackageManager.AddPackageAsync`, an empty dependency-package list, `DeploymentOptions.None`, current-user scope and no requested elevation. It exposes the exact path, file URI, identity, publisher, signer-certificate SHA-256, version, size, package digest, proposal digest and expiry. Approval starts unchecked. Approval rehashes and reverifies the package under the same lease, is single-use and can be cancelled.

For this milestone, approval returns only an in-memory validation receipt with `ExecutionPermitted=false` and `InstallerProcessesStarted=0`. There is no executor, process-broker operation, PowerShell invocation, package deployment API call or persisted authorization. The native fixture path is environment/root-gated, labels its trust evidence synthetic, and has no execution capability.

## Consequences

- Native UIA proves exact preview, default-unchecked denial, approval, cancellation, assertive status and zero child installer processes.
- A synthetic trust adapter proves adapter and UI behavior only. It is not Authenticode/MSIX trust evidence.
- A future executor must use a real Windows package trust verifier, consume a separately designed one-time authority, and reverify the final file immediately before activation under a continuous lease. This ADR does not authorize that work.
- Real signed MSIX installation, elevation behavior, prerequisite handling, update/uninstall/rollback, AV/vulnerability scanning, clean-VM and human assistive-technology acceptance remain blocked.

## Verified upstream contracts

- Microsoft MSIX documentation: <https://learn.microsoft.com/en-us/windows/msix/>
- Microsoft `PackageManager.AddPackageAsync`: <https://learn.microsoft.com/en-us/uwp/api/windows.management.deployment.packagemanager.addpackageasync?view=winrt-19041>
- Microsoft `DeploymentOptions`: <https://learn.microsoft.com/en-us/uwp/api/windows.management.deployment.deploymentoptions?view=winrt-19041>
- Microsoft MSIX signing overview: <https://learn.microsoft.com/en-us/windows/msix/package/signing-package-overview>
