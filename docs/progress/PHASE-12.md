# P12 — Full vertical end-to-end flows

- Status: BLOCKED (nine cross-phase local fixtures pass, including a controlled Facebook-shaped manual-login/current-post handoff; required live/provider/installer acceptance remains absent)
- Implementation commits: `1c51f9d`, `10ac274`, `10e07d5`, `84e7f23`, `ee7d8bf`, `7b0a03b`, `1caed88`, Facebook handoff `c200e8f`; P07 validator hardening `02d0564`; current exact source `02d0564`
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
8. Shared-viewport old-Facebook-post finder with explicit manual login/MFA/consent handoff, user-selected candidate, current-direct-post inspection and blocked-page no-fabrication proof against a prominently disclosed literal-loopback fixture, plus a sandbox-shaped report to genuine OOXML XLSX.
9. Exact digest-bound `.msix` request review with typed Windows deployment API preview, initially unchecked approval, denial, revalidation, cancellation and zero installer child processes.

Measured composed result: `P12 fixture scenarios passed=9`. Evidence manifest: `artifacts/test-results/p12-dd80f9ec1adf4390807fe6204ff9549c/fixture-scenarios.json` (generated from source `02d0564` and intentionally not committed). All nine validators exited 0; production actions remained 0. Its P11 composition includes 122/122 finance tests plus the authenticated child-Kestrel/SQLite-to-native-dashboard UIA gate at `artifacts/test-results/p11-7065dfe7ca624e8a898b00f8f7592740/`.

Focused Facebook/QuickBooks evidence: `artifacts/test-results/p12-integrations-4e9afdf28bd64db08986912dd8c77e7e/`. Exact TRX counters passed 17/17 selected integration tests, including 15 Facebook policy cases. Native UIA drove the complete fixture login handoff, search, candidate navigation and current-post inspection, verified nine distinct access keys and exact visible heading/date/media evidence, then navigated to a blocked fixture and proved the prior result address was cleared. Real Facebook requests, credentials read, cookies copied and production actions were all zero. The current-address result was the literal-loopback fixture, not a Facebook compatibility result.

The production workflow accepts only exact Facebook HTTPS hosts, opens a bounded search in DCx's own WebView2 profile, and requires the user to perform login/MFA/consent and choose a candidate before explicit handback. Only the current navigation/address can become a result. Supported direct post/video/permalink forms, visible article evidence, bounded semantic extraction, tracking removal, bidi/control stripping and navigation-generation consistency are required. Login, checkpoint, challenge, block, error, search-result and no-post states clear any prior result and expose no fabricated address. The product never reads password values, cookies or browser storage and does not copy Chrome/Edge sessions. Controlled fixtures require `DCX_P12_FACEBOOK_FIXTURE=1` plus an exact literal-loopback root and are default off. See ADR 0082 and `docs/accessibility/P12_FACEBOOK_POST_SEARCH_CHECKLIST.md`.

The XLSX fixture has a frozen header, filter, numeric currency cells, semantic text round-trip and formula-injection denial through inline strings. It makes zero QuickBooks network calls.

The successful package-review UI fixture uses an injected synthetic trust adapter and a noninstallable file. The product boundary holds and rehashes the exact package under a read lease, displays all meaningful bindings including signer-certificate SHA-256, and can return only an in-memory receipt with `ExecutionPermitted=false` and `InstallerProcessesStarted=0`. A separate real Windows Authenticode path denies the unsigned/incomplete fixture. Neither path has an executor, shell-controlled package path, process-broker install operation or deployment API call. See `docs/accessibility/P12_PACKAGE_INSTALL_CHECKLIST.md`, ADR 0029 and ADR 0030.

## Changed files in the latest milestone

- Facebook policy and state machine: `src/DCx.Core/Social/FacebookHistoricalPostSearch.cs` and `tests/DCx.Core.Tests/Social/FacebookHistoricalPostSearchTests.cs`.
- Bounded visible-semantic page inspection and accessible shared-viewport finder: `src/DCx.Desktop/Automation/FacebookHistoricalPostPageInspector.cs`, `src/DCx.Desktop/FacebookHistoricalPostSearchWindow.xaml`, `src/DCx.Desktop/FacebookHistoricalPostSearchWindow.xaml.cs`, and the shell wiring in `src/DCx.Desktop/MainWindow.xaml*`.
- Composed integration/blocked-page proof: `tests/DCx.WebFixtureServer/Program.cs`, `scripts/validate-p12-integrations.ps1`, and `scripts/validate-p12-fixtures.ps1`.
- Cancellation-order hardening discovered by the expanded gate: `src/DCx.Core/OpenAI/CodexAppServerClient.cs`, its focused tests and ADR 0083.
- P07 UIA wait hardening discovered by the first aggregate: `scripts/validate-p07.ps1` at `02d0564`.
- Current composed CARE fixture: isolated desktop startup/source lifecycle in `src/DCx.Desktop/App.xaml.cs` and `src/DCx.Desktop/CareContributorDashboardWindow.xaml.cs`; child Kestrel/SQLite plus native UIA orchestration in `scripts/validate-p11-contributor-loopback-ui.ps1`; disposal regression in `tests/DCx.Core.Tests/Finance/LoopbackCareContributorDashboardSourceTests.cs`.
- Exact package review and inert receipt policy: `src/DCx.Core/Deployment/PackageInstallReview.cs`.
- Accessible review window and validation-only startup route: `src/DCx.Desktop/PackageInstallReviewWindow.xaml`, `src/DCx.Desktop/PackageInstallReviewWindow.xaml.cs`, `src/DCx.Desktop/App.xaml.cs`.
- Policy/denial and native UIA gates: `tests/DCx.Core.Tests/Deployment/PackageInstallReviewTests.cs`, `scripts/validate-p12-package-approval.ps1`, `scripts/validate-p12-fixtures.ps1`.

## Blockers

- No live Facebook request, authenticated search or human login/MFA/consent acceptance record. Provider policy, current DOM compatibility and site stability remain unverified; the controlled fixture must not be cited as live compatibility.
- No QuickBooks sandbox OAuth or API retrieval. Only a sandbox-shaped client contract and export are validated.
- Deterministic top-document HTML5 web-video pause, visible-rectangle Q&A, continued pause and explicit resume pass. Live/authenticated/embedded/protected YouTube acceptance remains absent.
- Exact package/install approval behavior passes only as an inert local fixture. No real signed-package verifier, executor, elevation, install/update/uninstall/rollback or clean-VM acceptance exists.
- P09 Compose, P10 production CCB deployment/isolation and P11 real PayPal sandbox remain blocked.
- Live services require supported contracts, user authorization and separate human acceptance. Local fixture success is not counted as live acceptance.
