# P12 — Full vertical end-to-end flows

- Status: BLOCKED (seven cross-phase fixtures pass; required integrations/live acceptance remain absent)
- Partial validation commit: `1c51f9d`
- Documentation commit: recorded by the commit containing this file

## Reproduction and evidence

```bash
cd /mnt/d/DCx
powershell.exe -NoProfile -ExecutionPolicy Bypass -File D:\\DCx\\scripts\\validate-p12-fixtures.ps1
```

The recorded run passed seven composed validators with zero production actions:

1. Shared user-agent browser approval/takeover/handback fixture.
2. External Windows application/UIA handoff fixture.
3. Scanned-document OCR/export, local media frame description and safe YouTube browser-handoff fixture.
4. Private CKB/CCB insert/search/provenance/tested-reuse fixture.
5. Signed two-client global-replica process/HTTP simulation.
6. Consent/quarantine/contribution/qualified-receipt fixture.
7. CARE ledger and sandbox-only payout-safety mock.

Evidence manifest: `artifacts/test-results/p12-da97decbbd7b4cacbfd8c0419e6cb84f/fixture-scenarios.json` (generated test artifact, intentionally not committed).

## Blockers

- No Facebook-specific fixture/live authenticated search or human login/MFA acceptance record.
- No QuickBooks sandbox OAuth-to-XLSX integration.
- YouTube validation proves URL admission/browser handoff, not frame capture/Q&A from YouTube playback.
- No approved package installer execution/sandbox scenario.
- P09 Compose, P10 hostile CCB sandbox and P11 real PayPal sandbox remain blocked.
- Live services require separate supported contracts, user authorization and human acceptance. Adjacent fixture success is not counted as those scenarios.
