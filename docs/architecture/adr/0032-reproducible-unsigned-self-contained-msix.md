# ADR 0032: build a reproducible unsigned self-contained MSIX for local validation

- Status: Accepted for inert local validation; signing and installation remain blocked
- Date: 2026-10-17

## Context

The Windows SDK x64 `MakeAppx.exe` and `SignTool.exe` are installed under the 10.0.19041 kit even though neither directory is on `PATH`. Both have valid Microsoft Authenticode signatures. A release candidate still lacks an owner-approved production package identity, code-signing certificate, certificate-custody process, WebView2 bootstrap decision, clean Windows 11 VM and install authority.

P13 needs more evidence than a framework-dependent ZIP, but creating an unsigned package must not be confused with authorizing its installation or distribution. MakeAppx ZIP metadata also varies between invocations, so raw output is not byte-reproducible even when every entry is identical.

## Decision

1. Publish DCx as self-contained `win-x64`, embedding the pinned .NET Core and Windows Desktop 10.0.12 runtimes. Include the available runtime license and third-party notice files in the payload and SBOM/license inventory.
2. Use an explicitly synthetic `DCx.AgenticBrowser.LocalValidation` identity and `CN=DCx Local Validation` publisher. The manifest declares a per-user, medium-integrity, full-trust packaged classic WPF application. It is not a production identity.
3. Resolve the exact x64 MakeAppx path from the Windows Kits directory, reject reparse files, require a valid Microsoft Corporation Authenticode signer, and record the executable and signer-certificate SHA-256 values. Never invoke SignTool in this gate.
4. Admit only nonoverlapping regular paths beneath the repository artifact root. Reject existing outputs, roots, unsafe reparse ancestors or descendants, incomplete self-contained payloads, unsupported quoted paths and an unexpectedly bundled WebView2 executable.
5. Generate deterministic package assets and an ordered MakeAppx mapping. Run MakeAppx with typed arguments, a scrubbed OS-only environment, bounded output and time, and semantic validation enabled.
6. Canonicalize only local and central ZIP DOS timestamps through a bounded strict ZIP/ZIP64 parser. Then require MakeAppx to unpack the canonical package and compare the manifest and executable to the exact inputs. Two independent builds must have identical package length and SHA-256.
7. Require the resulting package to remain `NotSigned`, contain no `AppxSignature.p7x`, pass the production manifest inspector, and be denied by the real Windows Authenticode/trust verifier. Do not sign, install, register or execute it.
8. Do not bundle WebView2. The local-validation payload requires the Evergreen WebView2 Runtime already present on the tested Windows 11 machine. Missing-runtime detection and an approved accessible bootstrap/install path remain release blockers.

## Consequences

- P13 now exercises a real package envelope and MakeAppx semantic/unpack validation while preserving zero signing, installation and production actions.
- Self-contained .NET removes the .NET Desktop Runtime prerequisite for this x64 payload and increases its uncompressed size to about 177 MB.
- Canonicalization is deliberately narrow. Any unsupported ZIP structure, excessive entry count/size, mismatched offset, truncated field or failed post-canonicalization unpack fails closed.
- Secret scanning covers repository text, DCx-owned published executables/assemblies and every published text/configuration artifact. Microsoft/runtime and NuGet binary dependencies are instead governed by pinned inventory, notices, official advisory auditing and Defender scanning; arbitrary raw-byte regex hits in those binaries are not treated as secret proof.
- The synthetic identity, absent signature, absent WebView2 bootstrap, absent clean-VM lifecycle and absent human assistive-technology results keep P13 blocked. The artifact must not be distributed or described as an installer or release.

## Verified upstream contracts

- MakeAppx package creation: <https://learn.microsoft.com/en-us/windows/msix/package/create-app-package-with-makeappx-tool>
- Generate MSIX package components for desktop applications: <https://learn.microsoft.com/en-us/windows/msix/desktop/desktop-to-uwp-manual-conversion>
- WebView2 Runtime distribution: <https://learn.microsoft.com/en-us/microsoft-edge/webview2/concepts/distribution>
- MSIX signing overview: <https://learn.microsoft.com/en-us/windows/msix/package/signing-package-overview>
