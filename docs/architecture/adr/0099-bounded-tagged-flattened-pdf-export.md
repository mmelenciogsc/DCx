# ADR 0099: Bound URL intake and tagged flattened PDF export

- Status: Accepted for automated local fixture validation; PDF/UA, visual fidelity and human acceptance remain blocked
- Date: 2026-10-19

## Context

P12 requires an inaccessible PDF admitted from a URL, reviewed through the scanned-document wizard, and saved as a fillable or flattened PDF. The existing workflow started from a local file and exported semantic HTML/DOCX only. A production URL boundary must resist SSRF and ambiguous HTTP behavior. A generated PDF must also be complete before it replaces an existing destination and must expose stronger evidence than a `.pdf` extension or rendered appearance.

WebView2's supported `PrintToPdfAsync` API produced a complete fixture PDF but no `/StructTreeRoot` or `/MarkInfo`; that diagnostic output was rejected. Chromium's official DevTools `Page.printToPDF` contract exposes the experimental `generateTaggedPDF` and `generateDocumentOutline` parameters, so DCx uses that narrower bridge while treating it as a provisional compatibility boundary.

## Decision

- Production URL admission is public HTTPS only. User information, fragments, loopback/private/link-local/documentation addresses, redirects, cookies, proxies, ambient credentials and content decompression are denied. A custom connection callback resolves and connects only to a selected public address, preventing a later DNS result from silently bypassing address policy.
- Download requires exact `application/pdf`, bounded headers/connections/time, at most 100 MiB, `%PDF-` at the start and `%%EOF` at the end. Bytes stream into a random operation-private per-user directory. Closing the wizard disposes that handle and removes the downloaded temporary best-effort.
- Literal-loopback HTTP exists only for deterministic validation and requires both an explicit fixture flag and one exact injected origin. It is unavailable in normal product startup.
- Every exported block must have reviewed page geometry. A bounded builder encodes all untrusted text into script-free, external-resource-free CSP HTML with semantic headings, labels, tables and image descriptions positioned on 8.5-by-11-inch pages.
- A private, random WebView2 profile navigates only to that exact local HTML. Scripts, web messages, password/autofill features, context menus and developer UI are disabled. DCx invokes `Page.printToPDF` with tagged-PDF and outline generation requested, then admits only a bounded one-field JSON/base64 response.
- Decoded PDF bytes are zeroed after a same-directory atomic temporary write. Replacement is denied unless the result has `%PDF-`, `%%EOF`, `/StructTreeRoot` and `/MarkInfo`. Test-only PdfPig 0.1.16 then proves page count, extracted golden words and normalized reviewed coordinates.
- The UI calls the result a flattened PDF, states that it is non-editable, and says that human assistive-technology review remains required.

## Validation

Implementation commit `3b35701` and blocker correction `287660b` pass 23/23 focused document/download cases. Exact clean P07 evidence `artifacts/test-results/p07-7ff3868bfa104c639be624e38776e4b7/` records tagged structure, marked-content metadata, three extracted words, coordinate validation, atomic replacement and private-temporary cleanup. Exact clean P12 evidence `artifacts/test-results/p12-52a89969eea44bd8a09ca1f798f0632f/` passes all nine fixtures with zero production actions at `287660b`. Full native CI passes 641/641. P13 `artifacts/test-results/p13-ad83689e0aad493b9cefa877582908b9/` retains nine shipped components, proving test-only PdfPig did not enter the desktop SBOM.

## Consequences and residual risk

- The local automated URL-to-tagged-flattened-PDF branch is implemented; it is no longer listed as a missing P12 fixture.
- The output reconstructs reviewed OCR text and semantics at reviewed coordinates. It does not preserve the scanned raster background or non-text visual fidelity, does not emit fillable AcroForm fields, and is not claimed to conform to PDF/UA.
- `generateTaggedPDF` is an experimental DevTools parameter. WebView2/Chromium behavior may change and must remain gated by exact output validation.
- The DevTools result necessarily creates immutable managed JSON/base64 strings; secure erasure is not claimed. Decoded owned bytes are zeroed, while filesystem journals, backups, recovery tools and same-user observers can retain or observe source/output data.
- Public-address filtering reduces SSRF risk but does not establish safety of Windows PDF/OCR, WebView2/Chromium or test-inspector parsers against every malicious PDF. Same-user directory/reparse races and malicious local processes remain residual risks.
- Adobe Reader inspection, approved PDF/UA/conformance validation, source-image visual comparison and human JAWS/NVDA/Narrator acceptance remain blocked.
