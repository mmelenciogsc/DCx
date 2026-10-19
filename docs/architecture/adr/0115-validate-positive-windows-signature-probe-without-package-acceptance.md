# ADR 0115: validate the positive Windows signature probe without package acceptance

- Status: Accepted for read-only adapter evidence; signed DCx package acceptance remains blocked
- Date: 2026-10-20

## Context

ADR 0030 established strict MSIX manifest parsing and real Windows negative signature evidence. Its composed positive case remained injected. A read-only local inventory found one existing Microsoft-signed APPX archive, but Windows reported signer subject `CN=Microsoft Windows, O=Microsoft Corporation, L=Redmond, S=Washington, C=US` while its manifest publisher was `CN=Microsoft Corporation, O=Microsoft Corporation, L=Redmond, S=Washington, C=US`. Microsoft documents that a package-signing certificate subject must match the manifest publisher. DCx therefore rejected this archive and did not weaken exact equality, download another package, create a certificate, invoke SignTool or install anything.

The production PowerShell probe still needed real positive-output evidence. The absolute built-in Windows PowerShell executable is already present, Microsoft-signed and safe to inspect read-only. It is not an MSIX and cannot establish package acceptance.

## Decision

1. Exercise `WindowsPowerShellAuthenticodeProbe` against the exact built-in Windows PowerShell executable on Windows. Require status `Valid`, a trimmed control-free signer subject of 1–512 characters and a lowercase 64-character signer-certificate SHA-256.
2. Run the same check in the exact P13 validator and retain only the bounded public signer subject, certificate digest and status. Record `signed_msix_positive_acceptance=false` separately.
3. Continue to require exact signer-subject/manifest-publisher equality for composed package trust. The unsigned DCx local-validation package must remain `NotSigned` and denied.
4. For native denied-action testing, place keyboard focus on the invoked control before using UI Automation's programmatic Invoke pattern. This models keyboard activation and makes the subsequent approval-focus assertion meaningful. Retain explicit evidence that all three denial invocations originated with keyboard focus.

## Consequences

- DCx now has real positive and negative evidence for the bounded Windows signature-probe adapter, but only fixture-positive evidence for complete MSIX composition.
- The local signed APPX mismatch is evidence that `Valid` alone is insufficient, not a reason to relax package identity binding.
- No package was signed, accepted, installed, registered, activated, restarted, downloaded or published.
- Production identity/certificate custody, a genuinely signed DCx MSIX, revocation policy, accessible install/update/rollback/uninstall, clean-VM lifecycle, independent assessment and human JAWS/NVDA/Narrator acceptance remain P13 blockers.

## Validation

Implementation commits `eb6b769` and `69a069b` pass 22/22 package-trust cases and the full 683/683 Windows suite. Exact P13 evidence is `artifacts/test-results/p13-f4cb4cdf196f41c0820a394b22335dfe/`; the positive probe returned `Valid`, signed-MSIX acceptance remained false and every controlled-update authority counter remained zero. One clean-source rerun first exposed an unfocused programmatic staging invocation; after hardening, the isolated native flow passed once and then five consecutive repetitions before the final exact-source P13 run passed. Exact P14 evidence `artifacts/test-results/p14-add665c23ffd4620aa89bb2f91929ad7/` remains `NO_GO` with seven blockers and activation false.

## Verified upstream contracts

- Microsoft package signing overview: <https://learn.microsoft.com/en-us/windows/msix/package/signing-package-overview>
- Microsoft package-signing certificate subject requirement: <https://learn.microsoft.com/en-us/windows/msix/package/create-certificate-package-signing#determine-the-subject-of-your-packaged-app>
- Microsoft `Get-AuthenticodeSignature`: <https://learn.microsoft.com/en-us/powershell/module/microsoft.powershell.security/get-authenticodesignature?view=powershell-5.1>
