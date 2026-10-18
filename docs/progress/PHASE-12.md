# P12 — Full vertical end-to-end flows

- Status: BLOCKED (nine cross-phase local fixtures pass; required live/provider/installer acceptance remains absent)
- Implementation commits: `1c51f9d`, `10ac274`, `10e07d5`, `84e7f23`, `ee7d8bf`, `7b0a03b`; P07 export-race fix `0ccf5e8`; current composed source `1caed88`
- Documentation commit: recorded by the commit containing this file

## Reproduction and evidence

```bash
cd /mnt/d/DCx
pwsh.exe -NoProfile -ExecutionPolicy Bypass -File D:\DCx\scripts\validate-p12-package-approval.ps1
pwsh.exe -NoProfile -ExecutionPolicy Bypass -File D:\DCx\scripts\validate-p12-fixtures.ps1
```

The recorded run passed nine composed validators with zero production actions:

1. Shared user-agent browser approval/takeover/handback fixture.
2. External Windows application/UIA handoff fixture.
3. Scanned-document OCR/export, local media frame description/question, safe YouTube browser handoff, and shared-viewport visible-rectangle Q&A against a generated HTML5 web video.
4. Private CKB/CCB insert/search/provenance/tested-reuse fixture.
5. Signed two-client global-replica process/HTTP simulation.
6. Consent/quarantine/contribution/qualified-receipt fixture.
7. Durable CARE ledger and sandbox-only payout-safety mock.
8. Local, explicitly non-Facebook manual-login/old-post search through the live WebView2 UIA tree plus sandbox-shaped report to genuine OOXML XLSX.
9. Exact digest-bound `.msix` request review with typed Windows deployment API preview, initially unchecked approval, denial, revalidation, cancellation and zero installer child processes.

Measured composed result: `P12 fixture scenarios passed=9`. Evidence manifest: `artifacts/test-results/p12-912b46b79f3043d8867ee87d75eda7eb/fixture-scenarios.json` (generated from source `1caed88` and intentionally not committed). All nine validators exited 0; production actions remained 0. Its P11 composition includes 122/122 finance tests plus the authenticated child-Kestrel/SQLite-to-native-dashboard UIA gate. Focused package-review evidence remains generated and intentionally not committed.

The XLSX fixture has a frozen header, filter, numeric currency cells, semantic text round-trip and formula-injection denial through inline strings. It makes zero QuickBooks network calls. The social fixture exposes a dated post, meaningful image alternative text and labeled video, but is deliberately not described as Facebook compatibility.

The successful package-review UI fixture uses an injected synthetic trust adapter and a noninstallable file. The product boundary holds and rehashes the exact package under a read lease, displays all meaningful bindings including signer-certificate SHA-256, and can return only an in-memory receipt with `ExecutionPermitted=false` and `InstallerProcessesStarted=0`. A separate real Windows Authenticode path denies the unsigned/incomplete fixture. Neither path has an executor, shell-controlled package path, process-broker install operation or deployment API call. See `docs/accessibility/P12_PACKAGE_INSTALL_CHECKLIST.md`, ADR 0029 and ADR 0030.

## Changed files in the latest milestone

- Current composed CARE fixture: isolated desktop startup/source lifecycle in `src/DCx.Desktop/App.xaml.cs` and `src/DCx.Desktop/CareContributorDashboardWindow.xaml.cs`; child Kestrel/SQLite plus native UIA orchestration in `scripts/validate-p11-contributor-loopback-ui.ps1`; disposal regression in `tests/DCx.Core.Tests/Finance/LoopbackCareContributorDashboardSourceTests.cs`.
- Exact package review and inert receipt policy: `src/DCx.Core/Deployment/PackageInstallReview.cs`.
- Accessible review window and validation-only startup route: `src/DCx.Desktop/PackageInstallReviewWindow.xaml`, `src/DCx.Desktop/PackageInstallReviewWindow.xaml.cs`, `src/DCx.Desktop/App.xaml.cs`.
- Policy/denial and native UIA gates: `tests/DCx.Core.Tests/Deployment/PackageInstallReviewTests.cs`, `scripts/validate-p12-package-approval.ps1`, `scripts/validate-p12-fixtures.ps1`.

## Blockers

- No live Facebook authenticated search or human login/MFA acceptance record. Site automation stability and policy remain unverified.
- No QuickBooks sandbox OAuth or API retrieval. Only a sandbox-shaped client contract and export are validated.
- Deterministic top-document HTML5 web-video pause, visible-rectangle Q&A, continued pause and explicit resume pass. Live/authenticated/embedded/protected YouTube acceptance remains absent.
- Exact package/install approval behavior passes only as an inert local fixture. No real signed-package verifier, executor, elevation, install/update/uninstall/rollback or clean-VM acceptance exists.
- P09 Compose, P10 production CCB deployment/isolation and P11 real PayPal sandbox remain blocked.
- Live services require supported contracts, user authorization and separate human acceptance. Local fixture success is not counted as live acceptance.
