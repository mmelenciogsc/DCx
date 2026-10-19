# P12 — Full vertical end-to-end flows

- Status: BLOCKED (nine cross-phase local fixtures pass, including strict URL-to-tagged-flattened-PDF, strict local QuickBooks XLSX and controlled Facebook-shaped handoff; live-provider/installer, PDF conformance/visual fidelity and human acceptance remain absent)
- Implementation commits: `1c51f9d`, `10ac274`, `10e07d5`, `84e7f23`, `ee7d8bf`, `7b0a03b`, `1caed88`, Facebook handoff `c200e8f`; P07 validator hardening `02d0564`; Ollama sequencing `2713413`; notification-bound cancellation `5614991`; live local-resource admission and verified cleanup `94a0f6b`; atomic semantic document export `5931485`; strict local QuickBooks XLSX `da4187a`; strict URL/tagged flattened PDF `3b35701`; exact clean-source aggregate binding/trust and blocker corrections `f6fa77e`, `c3360af`, `62914d1`, `287660b`; current exact source `287660b`
- Documentation commit: recorded by the commit containing this file

## Reproduction and evidence

```bash
cd /mnt/d/DCx
pwsh.exe -NoProfile -ExecutionPolicy Bypass -File D:\\DCx\\scripts\\validate-p12-package-approval.ps1
pwsh.exe -NoProfile -ExecutionPolicy Bypass -File D:\\DCx\\scripts\\validate-p12-fixtures.ps1
```

The recorded run passed nine composed validators with zero production actions:

1. Shared user-agent browser approval/takeover/handback fixture.
2. External Windows application/UIA handoff fixture.
3. Strict scanned-PDF URL download/private cleanup, OCR/semantic review, HTML/DOCX/tagged-flattened-PDF export, local media frame description/question, safe YouTube browser handoff, and shared-viewport visible-rectangle Q&A against a generated HTML5 web video.
4. Private CKB/CCB insert/search/provenance/tested-reuse fixture.
5. Signed two-client global-replica process/HTTP simulation.
6. Consent/quarantine/contribution/qualified-receipt fixture.
7. Durable CARE ledger and sandbox-only payout-safety mock.
8. Shared-viewport old-Facebook-post finder with explicit manual login/MFA/consent handoff, user-selected candidate, current-direct-post inspection and blocked-page no-fabrication proof against a prominently disclosed literal-loopback fixture, plus a sandbox-shaped report to genuine OOXML XLSX.
9. Exact digest-bound `.msix` request review with typed Windows deployment API preview, initially unchecked approval, denial, revalidation, cancellation and zero installer child processes.

Measured composed result: `P12 fixture scenarios passed=9`. Evidence manifest: `artifacts/test-results/p12-52a89969eea44bd8a09ca1f798f0632f/fixture-scenarios.json` (generated from exact clean implementation source `287660b` and intentionally not committed). The validator refuses a dirty/changing tracked worktree and records the full commit. All nine validators exited 0; production actions remained 0. Its P11 composition includes 127/127 finance tests plus native recipient/operations/contributor UIA at `artifacts/test-results/p11-f3bf4930b8ec4ed7b8dc99924841cc07/`.

Focused Facebook/QuickBooks evidence: `artifacts/test-results/p12-integrations-c1dd23e0a84e45cfbb3b6ec131bba78b/`. Exact TRX counters passed 24/24 selected integration cases: 15 Facebook policy cases and nine QuickBooks cases. Native UIA drove the complete fixture login handoff, search, candidate navigation and current-post inspection, verified nine distinct access keys and exact visible heading/date/media evidence, then navigated to a blocked fixture and proved the prior result address was cleared. The QuickBooks cases prove Office 2007 schema validity, table/relationship structure, a fixed subtotal formula, typed date/USD formats, Excel's 1900 serial boundary, formula-injection denial and atomic failure/cancellation behavior. Real Facebook/QuickBooks requests, credentials read, cookies copied and production actions were all zero. The current-address result was the literal-loopback fixture, not a Facebook compatibility result. Inert package-review evidence is `artifacts/test-results/p12-package-approval-abda2427fda84298ad6fb3c157a698b0/`; 12/12 passed without install or production action.

Document evidence: `artifacts/test-results/p07-7ff3868bfa104c639be624e38776e4b7/`. Native UIA proved strict URL download into an operation-private temporary, Windows OCR golden text/geometry, reviewed repair, atomic HTML/DOCX/tagged flattened PDF replacement, PDF structure/marked-content markers, extracted `DCX ACCESSIBLE 42`, normalized reviewed coordinates and source-temporary cleanup. Focused document/download cases passed 23/23. The first supported WebView2 print attempt was complete but lacked structure markers and was rejected; the accepted path uses Chromium's experimental tagged-print contract and strict post-validation. ADRs 0097/0099 and `docs/accessibility/P12_TAGGED_PDF_CHECKLIST.md` distinguish automated flattened-text evidence from PDF/UA, source-raster visual fidelity and human acceptance.

The production workflow accepts only exact Facebook HTTPS hosts, opens a bounded search in DCx's own WebView2 profile, and requires the user to perform login/MFA/consent and choose a candidate before explicit handback. Only the current navigation/address can become a result. Supported direct post/video/permalink forms, visible article evidence, bounded semantic extraction, tracking removal, bidi/control stripping and navigation-generation consistency are required. Login, checkpoint, challenge, block, error, search-result and no-post states clear any prior result and expose no fabricated address. The product never reads password values, cookies or browser storage and does not copy Chrome/Edge sessions. Controlled fixtures require `DCX_P12_FACEBOOK_FIXTURE=1` plus an exact literal-loopback root and are default off. See ADR 0082 and `docs/accessibility/P12_FACEBOOK_POST_SEARCH_CHECKLIST.md`.

The XLSX fixture now has a named SpreadsheetML table, frozen header, filter, typed numeric date/USD cells, explicit formats, a product-owned `SUBTOTAL(109,[Amount USD])` totals formula with cached checked value, semantic text round-trip and formula-injection denial through inline strings. Input labels, dates, row count and amounts are bounded; invalid XML/control/bidi content, wrong extension, cancellation and replacement failures preserve the old destination. Microsoft Open XML SDK 3.5.1 validates the generated package against the Office 2007 schema and remains test-only. It makes zero QuickBooks network calls. This closes local formula/table/date/currency conformance, not real sandbox OAuth/API retrieval, Microsoft Excel interoperability or human screen-reader acceptance. See ADR 0098 and `docs/accessibility/P12_QUICKBOOKS_XLSX_CHECKLIST.md`.

The successful package-review UI fixture uses an injected synthetic trust adapter and a noninstallable file. The product boundary holds and rehashes the exact package under a read lease, displays all meaningful bindings including signer-certificate SHA-256, and can return only an in-memory receipt with `ExecutionPermitted=false` and `InstallerProcessesStarted=0`. A separate real Windows Authenticode path denies the unsigned/incomplete fixture. Neither path has an executor, shell-controlled package path, process-broker install operation or deployment API call. See `docs/accessibility/P12_PACKAGE_INSTALL_CHECKLIST.md`, ADR 0029 and ADR 0030.

## Changed files in the latest milestone

- Facebook policy and state machine: `src/DCx.Core/Social/FacebookHistoricalPostSearch.cs` and `tests/DCx.Core.Tests/Social/FacebookHistoricalPostSearchTests.cs`.
- Bounded visible-semantic page inspection and accessible shared-viewport finder: `src/DCx.Desktop/Automation/FacebookHistoricalPostPageInspector.cs`, `src/DCx.Desktop/FacebookHistoricalPostSearchWindow.xaml`, `src/DCx.Desktop/FacebookHistoricalPostSearchWindow.xaml.cs`, and the shell wiring in `src/DCx.Desktop/MainWindow.xaml*`.
- Composed integration/blocked-page proof: `tests/DCx.WebFixtureServer/Program.cs`, `scripts/validate-p12-integrations.ps1`, and `scripts/validate-p12-fixtures.ps1`.
- Cancellation-order hardening discovered by expanded gates: `src/DCx.Core/OpenAI/CodexAppServerClient.cs`, its focused tests and ADRs 0083/0085. The exact current gate binds cancellation from the first exact turn notification and rejects a conflicting response identity.
- P06/P07 local Ollama work shares the current-user cross-process inference lease through live resource admission, complete vision/embedding work and verified exact-model unload; ADRs 0084 and 0086 record the cooperative and resource boundaries.
- P07 UIA wait hardening discovered by the first aggregate: `scripts/validate-p07.ps1` at `02d0564`.
- Current composed CARE fixture: isolated desktop startup/source lifecycle in `src/DCx.Desktop/App.xaml.cs` and `src/DCx.Desktop/CareContributorDashboardWindow.xaml.cs`; child Kestrel/SQLite plus native UIA orchestration in `scripts/validate-p11-contributor-loopback-ui.ps1`; disposal regression in `tests/DCx.Core.Tests/Finance/LoopbackCareContributorDashboardSourceTests.cs`.
- Exact package review and inert receipt policy: `src/DCx.Core/Deployment/PackageInstallReview.cs`.
- Accessible review window and validation-only startup route: `src/DCx.Desktop/PackageInstallReviewWindow.xaml`, `src/DCx.Desktop/PackageInstallReviewWindow.xaml.cs`, `src/DCx.Desktop/App.xaml.cs`.
- Policy/denial and native UIA gates: `tests/DCx.Core.Tests/Deployment/PackageInstallReviewTests.cs`, `scripts/validate-p12-package-approval.ps1`, `scripts/validate-p12-fixtures.ps1`.
- Bounded language-aware semantic export and atomic replacement: `src/DCx.Core/Documents/AccessibleDocument.cs`, `AccessibleDocumentValidator.cs`, `AccessibleDocumentExporter.cs`, `src/DCx.Desktop/Documents/WindowsOcrService.cs`, `src/DCx.Desktop/ScannedPdfWizardWindow.xaml.cs` and `tests/DCx.Core.Tests/Documents/AccessibleDocumentTests.cs`.
- Strict public-HTTPS downloader, semantic flattened layout and tagged-PDF bridge: `src/DCx.Core/Documents/ScannedPdfUrlDownloader.cs`, `AccessiblePdfHtmlBuilder.cs`, `src/DCx.Desktop/Documents/WebView2AccessiblePdfExporter.cs`, the content-viewer/wizard UI, hostile downloader/document tests, test-only `tests/DCx.PdfInspector/`, fixture server and `scripts/validate-p07.ps1`.
- Strict SpreadsheetML table/formula/date/currency writer, hostile/atomic/schema cases and test-only official validator: `src/DCx.Core/Integrations/QuickBooksSandboxExport.cs`, `tests/DCx.Core.Tests/Integrations/QuickBooksSandboxExportTests.cs`, `Directory.Packages.props`, the test project/lock file and `scripts/validate-p12-integrations.ps1`.
- Exact source/evidence binding: `scripts/validate-p07.ps1` and `scripts/validate-p12-fixtures.ps1`. The first aggregate attempt after adding binding stopped before scenario 1 because Windows Git rejected the WSL-owned NTFS repository as dubious. Commit `c3360af` supplies repository trust only to each Git command; it changes no global Git configuration and the clean exact-source rerun passed.

## Blockers

- No live Facebook request, authenticated search or human login/MFA/consent acceptance record. Provider policy, current DOM compatibility and site stability remain unverified; the controlled fixture must not be cited as live compatibility.
- No authorized QuickBooks sandbox OAuth/API retrieval, actual provider-response mapping, Microsoft Excel interoperability or human Excel/JAWS/NVDA/Narrator acceptance. The strict local workbook conformance gate is not live-provider acceptance.
- The automated URL-to-tagged-flattened-textual-PDF branch passes, including extracted text and coordinate evidence. It does not preserve source raster/non-text visual fidelity, emit fillable AcroForm fields or prove PDF/UA, Adobe Reader or human JAWS/NVDA/Narrator acceptance.
- Deterministic top-document HTML5 web-video pause, visible-rectangle Q&A, continued pause and explicit resume pass. Live/authenticated/embedded/protected YouTube acceptance remains absent.
- Exact package/install approval behavior passes only as an inert local fixture. No real signed-package verifier, executor, elevation, install/update/uninstall/rollback or clean-VM acceptance exists.
- P09 Compose, P10 production CCB deployment/isolation and P11 real PayPal sandbox remain blocked.
- Human JAWS, NVDA and Narrator acceptance is absent. Live services require supported contracts, user authorization and separate human acceptance. Local fixture success is not counted as live acceptance.
