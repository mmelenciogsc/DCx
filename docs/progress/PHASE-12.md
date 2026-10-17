# P12 — Full vertical end-to-end flows

- Status: BLOCKED (eight cross-phase local fixtures pass; required live/provider/installer acceptance remains absent)
- Implementation commits: `1c51f9d`, `10ac274`, `10e07d5`; P07 export-race fix `0ccf5e8`
- Documentation commit: recorded by the commit containing this file

## Reproduction and evidence

```bash
cd /mnt/d/DCx
pwsh.exe -NoProfile -ExecutionPolicy Bypass -File D:\DCx\scripts\validate-p12-fixtures.ps1
```

The recorded run passed eight composed validators with zero production actions:

1. Shared user-agent browser approval/takeover/handback fixture.
2. External Windows application/UIA handoff fixture.
3. Scanned-document OCR/export, local media frame description/question, safe YouTube browser handoff, and shared-viewport visible-rectangle Q&A against a generated HTML5 web video.
4. Private CKB/CCB insert/search/provenance/tested-reuse fixture.
5. Signed two-client global-replica process/HTTP simulation.
6. Consent/quarantine/contribution/qualified-receipt fixture.
7. Durable CARE ledger and sandbox-only payout-safety mock.
8. Local, explicitly non-Facebook manual-login/old-post search through the live WebView2 UIA tree plus sandbox-shaped report to genuine OOXML XLSX.

Measured composed result: `P12 fixture scenarios passed=8`. Evidence manifest: `artifacts/test-results/p12-36617c643d804b7eb9c973e40803e63a/fixture-scenarios.json` (generated and intentionally not committed). All eight validators exited 0; production actions remained 0.

The XLSX fixture has a frozen header, filter, numeric currency cells, semantic text round-trip and formula-injection denial through inline strings. It makes zero QuickBooks network calls. The social fixture exposes a dated post, meaningful image alternative text and labeled video, but is deliberately not described as Facebook compatibility.

## Blockers

- No live Facebook authenticated search or human login/MFA acceptance record. Site automation stability and policy remain unverified.
- No QuickBooks sandbox OAuth or API retrieval. Only a sandbox-shaped client contract and export are validated.
- Deterministic top-document HTML5 web-video pause, visible-rectangle Q&A, continued pause and explicit resume pass. Live/authenticated/embedded/protected YouTube acceptance remains absent.
- No approved package installer execution/sandbox scenario.
- P09 Compose, P10 production CCB deployment/isolation and P11 real PayPal sandbox remain blocked.
- Live services require supported contracts, user authorization and separate human acceptance. Local fixture success is not counted as live acceptance.
