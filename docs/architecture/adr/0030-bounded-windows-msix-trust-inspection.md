# ADR 0030: bounded Windows MSIX trust inspection before installation approval

- Status: Accepted for read-only admission; real signed-package acceptance remains blocked
- Date: 2026-10-17

## Context

ADR 0029 established an accessible, non-executing package-review boundary, but its successful UI fixture used synthetic trust evidence. A future installation decision must not trust caller-provided identity, publisher, version or a Boolean signature claim. DCx needs a real Windows read-only denial path before it can safely consider any deployment authority.

The machine has the supported Windows PowerShell `Get-AuthenticodeSignature` cmdlet. Microsoft documents the MSIX manifest `Identity` fields and requires a package publisher that corresponds to its signing certificate. No owner-authorized signed DCx MSIX, signing certificate or clean test VM is available.

## Decision

Inspect the exact leased `.msix` through two independent, fail-closed views:

1. Parse exactly one root `AppxManifest.xml` from a bounded ZIP container. Require the Windows 10 foundation namespace and one direct `Identity` with a bounded valid name, publisher and exact four-part version. Reject excessive entries, oversized/compression-abusive manifests, case-variant duplicates, missing/ambiguous identity, unsupported namespace, DTD/XXE and malformed XML.
2. Run the built-in Windows PowerShell 5.1 executable by absolute system path with a constant encoded command. Pass the package path only through a scrubbed child-process environment, never command text or arguments. Disable profiles and interaction; bound time and both output streams; kill the process tree on timeout or output flood; decode a strict, closed JSON envelope containing only signature status, signer subject and SHA-256 of the signer certificate.

`Valid` is necessary but not sufficient. The signer subject must exactly match the authenticated manifest publisher, and the signer certificate SHA-256 becomes part of the canonical approval proposal and its second revalidation. Every other signature status, missing certificate, mismatch or malformed probe result denies trust.

## Consequences

- Windows now performs a real read-only signature check against the synthetic package and returns a nonvalid state (`UnknownError` on the current incomplete container); DCx denies it.
- Fixture probes validate the composition logic for a matching `Valid` result, but are not real positive signature evidence.
- The approval preview now exposes and binds the signer-certificate SHA-256 in addition to identity, publisher, version, package digest and proposal digest.
- No package is installed, registered, staged with Windows, elevated or activated. The process broker still has no install operation.
- Positive acceptance requires an authorized genuinely signed MSIX whose certificate, publisher, identity and version can be independently verified. Signed install/update/uninstall/rollback, revocation/network policy, clean-VM and human assistive-technology acceptance remain blocked.

## Verified upstream contracts

- Microsoft `Get-AuthenticodeSignature`: <https://learn.microsoft.com/en-us/powershell/module/microsoft.powershell.security/get-authenticodesignature?view=powershell-5.1>
- Microsoft MSIX signing: <https://learn.microsoft.com/en-us/windows/msix/package/signing-package-overview>
- Microsoft Appx manifest `Identity`: <https://learn.microsoft.com/en-us/uwp/schemas/appxpackage/uapmanifestschema/element-identity>
