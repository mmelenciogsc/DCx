# ADR 0098: Export QuickBooks-shaped reports as strict local SpreadsheetML tables

- Status: Accepted for local sandbox-shaped workbook conformance; live QuickBooks and human acceptance remain blocked
- Date: 2026-10-19

## Context

P12 requires a real XLSX with formulas, a table, dates and currency data. The earlier local writer produced an OOXML package with numeric amounts, but it had no SpreadsheetML table, trusted total formula or typed date cells. It also lacked schema conformance evidence and bounded validation for hostile report labels. A local writer cannot establish QuickBooks OAuth/API compatibility or Excel/screen-reader usability.

## Decision

- Admit only a nonempty USD sandbox report with a bounded report identifier, valid 1900-or-later date range, 1–10,000 bounded rows and bounded amounts. Reject XML-invalid, control and bidirectional-formatting characters.
- Emit one internal SpreadsheetML table named `ProfitAndLossTable`, typed numeric date and USD cells, explicit date/currency formats, and a fixed `SUBTOTAL(109,[Amount USD])` totals formula with a cached checked sum.
- Treat every report-supplied label as an inline string. No report field can become a formula, relationship, hyperlink, macro or external reference.
- Preserve Excel's serial-date compatibility boundary explicitly: 1900-01-01 is 1, 1900-02-28 is 59 and 1900-03-01 is 61.
- Write a random same-directory temporary and replace the destination only after serialization, flush and cancellation checks. A failed or canceled export preserves the prior destination and removes the temporary best-effort.
- Validate generated workbooks in tests with Microsoft Open XML SDK 3.5.1 against the Office 2007 schema. Keep this MIT-licensed validator test-only with `PrivateAssets=all`; it is not shipped in the desktop package.

This decision makes no QuickBooks request, reads no credential and grants no live-provider authority. Open XML schema validity is not Microsoft Excel interoperability or JAWS/NVDA/Narrator acceptance.

## Validation

Implementation commit `da4187a` passes 9/9 QuickBooks cases within the 24/24 focused P12 integration gate. Current evidence `artifacts/test-results/p12-integrations-3236f588a7a64c1a98630a6df470a656/` records schema, table, formula, typed date/currency and formula-injection controls with zero QuickBooks requests. Clean-source aggregate `artifacts/test-results/p12-a296e53f71694a5da5b7bde57fef62f8/` passes nine scenarios at `1528e60`; full native CI passes 641/641. P13 evidence `p13-c22d414a7d7f4accb440005818866e0c/` retains nine shipped components, confirming test-only Open XML SDK and PdfPig dependencies did not enter the desktop SBOM.

## Consequences and residual risk

- The local workbook portion of P12's QuickBooks scenario now has deterministic automated conformance evidence.
- Formula-looking account names remain literal text, and the package has no external relationship.
- User-selected workbook and temporary storage contain financial labels and amounts. Same-user directory attacks, filesystem rollback, journaling, backup retention and secure deletion are outside this boundary.
- Authorized QuickBooks sandbox OAuth/API retrieval, mapping of actual provider responses, Microsoft Excel interoperability, visual review and human JAWS/NVDA/Narrator acceptance remain blocked.
