# P12 tagged flattened PDF accessibility checklist

Automated structure and extraction checks do not substitute for PDF/UA validation or human screen-reader review.

## Automated evidence

- [x] The URL field and download action expose stable names, access keys and keyboard focus through native UIA.
- [x] Normal product mode permits public HTTPS only; the deterministic HTTP loopback origin requires an explicit exact fixture gate.
- [x] The downloaded PDF remains in a random private operation directory and is removed after the wizard closes.
- [x] Every flattened block requires reviewed page geometry and semantic validation before generation.
- [x] Untrusted content is encoded into CSP-locked local HTML with no script or external resource.
- [x] An existing destination is replaced only after a complete PDF is written and flushed; failed generation preserves the prior file.
- [x] The generated PDF contains `/StructTreeRoot` and `/MarkInfo`.
- [x] Test-only PdfPig extracts `DCX ACCESSIBLE 42` and confirms the reviewed normalized position on one page.
- [x] The OCR-reviewed source SHA-256 is reverified under the render read lease; the generated PDF contains one high-resolution source raster at its original aspect and text coordinates transformed into the same placement.
- [x] Test-only independent decode/reference comparison passes bounded RMSE and changed-pixel thresholds for three generated monochrome/color, portrait/landscape pages, while each page's RGB-inverted control is rejected.
- [x] Mutating the source after OCR review denies export before replacement, leaves the prior destination unchanged, and permits export only after restoring the exact reviewed bytes.
- [x] The UI identifies the result as non-editable and announces completion/failure through an accessible status.
- [x] Test-only PdfPig is excluded from the shipped P13 SBOM.

## Human and conformance acceptance still required

- [ ] Adobe Acrobat/Reader accessibility inspection, reading order and document-language review.
- [ ] An approved PDF/UA validator with all findings resolved or explicitly adjudicated.
- [ ] JAWS with Adobe Reader: title, reading order, headings, labels, tables and image descriptions.
- [ ] NVDA with Adobe Reader or another supported viewer: the same checks.
- [ ] Narrator with a supported PDF viewer: the same checks.
- [ ] Keyboard-only URL intake, OCR repair, repeated export and failure-recovery review.
- [ ] Independent pixel/visual comparison with a representative real-world scanned-document corpus, including photographic, skewed, damaged and unusual-color-profile inputs.

## Deliberate current limits

- [x] Establish bounded positive and negative raster-comparison thresholds for the generated three-page fixture rather than relying only on embedded-image dimensions, placement and aspect.
- [ ] Establish reviewed thresholds across a representative real-world corpus and supported WebView2/PDF-decoder versions.
- [ ] Implement fillable AcroForm fields if product requirements select a fillable rather than flattened derivative.

The passing fixture supports a tagged flattened derivative with three aspect-preserved rendered source rasters and independent per-page pixel evidence for that generated corpus. It must not be represented as representative real-world document fidelity, PDF/UA-conformant, fillable or human accepted.
