# ADR 0097: Replace reviewed document exports atomically with explicit semantics

- Status: Accepted for semantic HTML/DOCX local export; saved accessible PDF remains blocked
- Date: 2026-10-19

## Context

The scanned-PDF wizard exported real HTML and OPC/DOCX files, but the DOCX referenced `Title` and `Heading1` styles that it did not define, omitted language and core-title metadata, flattened table/image-review semantics into ordinary paragraphs, and failed when the fixed export path already existed. Direct HTML writes could also leave a partial replacement after cancellation or storage failure. Those behaviors were weaker than the P07 accessibility gate and did not satisfy P12's separate URL-to-fillable/flattened-PDF scenario.

## Decision

- Validate a bounded block count and total text size, bounded identifiers/text/labels, XML-safe characters, unique reading order and a recognized non-invariant language tag before creating export storage.
- Build HTML and DOCX into a random same-directory temporary file. Replace the destination only after complete serialization, flush and cancellation checks; best-effort cleanup removes the temporary on every failure.
- Emit explicit DOCX relationships, core title/language metadata, language defaults and defined `Title`, outline-level `Heading1`, `Caption` and default table styles. Preserve reviewed image-description labels as caption text and reviewed table blocks as actual one-cell WordprocessingML tables. Add no external relationship.
- Propagate the Windows OCR engine language into both HTML and DOCX instead of asserting a fixed document language.
- Disable both export actions while one runs, announce preparation/completion through the existing live status, and report a stable safe failure while preserving any prior export.

This decision does not claim tagged-PDF, AcroForm, PDF/UA, Microsoft Word interoperability, human screen-reader acceptance or the authoritative P12 fillable/flattened-PDF scenario.

## Validation

Exact source `c3360af` passes 10/10 focused document tests, including hostile XML characters, cancellation, extension confusion, atomic replacement, semantic package parts and no external relationships. Native evidence `artifacts/test-results/p07-54c14d6c50d44a38a546304f491e39b8/` proves Windows OCR, reviewed semantic repair, atomic DOCX replacement, title/heading/language semantics and viewer round-trip. P12 passes nine composed fixtures at `artifacts/test-results/p12-962a4406bfdb49ad865cabba56798617/`; full native CI passes 621/621 with zero build warnings/errors.

## Consequences and residual risk

- Repeated exports no longer require manual deletion, and failed/canceled serialization cannot replace a valid prior file through the product boundary.
- XML-invalid and excessive document graphs fail before an output file is created.
- DOCX is a structured derivative, not the required saved accessible PDF. No Microsoft Office Open XML conformance tool, Microsoft Word, PDF/UA validator or human JAWS/NVDA/Narrator pass has accepted the artifact.
- Same-user filesystem replacement/race resistance beyond ordinary same-directory atomic rename is not claimed. Export destinations remain user-selected local storage and need normal OS access protection.
