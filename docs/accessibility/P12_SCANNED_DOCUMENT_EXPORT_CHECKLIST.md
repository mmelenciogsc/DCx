# P12 scanned-document export accessibility checklist

Automated Windows UIA status does not substitute for a human screen-reader or document-application review.

## Automated evidence

- [x] Windows OCR recognizes generated golden text and emits normalized page geometry.
- [x] The reviewed semantic-role and accessible-label repair remains present in semantic HTML and DOCX.
- [x] Export actions have stable UIA identities and remain keyboard-invocable.
- [x] The assertive status announces preparation, safe failure and atomic completion.
- [x] Both export actions are disabled during one export and re-enabled only for a valid reviewed document.
- [x] A prior DOCX is replaced only by a complete ZIP/OPC package; no same-directory temporary remains.
- [x] DOCX contains defined title, heading-outline, caption and table styles plus title/language metadata and a styles relationship.
- [x] DOCX round-trips through DCx's bounded Office text extractor and accessible viewer.
- [x] Invalid XML characters, cancellation and mismatched output extensions preserve the prior destination.
- [x] No external DOCX relationship is emitted.

## Human acceptance still required

- [ ] JAWS with Microsoft Word: title, heading navigation, reading order, table announcement and caption comprehension.
- [ ] NVDA with Microsoft Word or another supported editor: the same checks.
- [ ] Narrator with Microsoft Word or another supported editor: the same checks.
- [ ] Keyboard-only review of OCR blocks, semantic repair and repeated export/failure recovery.
- [ ] Visual comparison of source PDF geometry and repaired output.

## Tagged flattened PDF extension

- [x] Admit a strictly bounded URL and delete the private downloaded temporary after wizard close.
- [x] Produce an atomically replaced tagged flattened PDF with digest-bound aspect-preserved source raster, extracted-text, coordinate, `/StructTreeRoot` and `/MarkInfo` evidence.
- [ ] Independently compare representative source/output pixels and provide fillable AcroForm fields if that branch is selected.
- [ ] Pass an approved PDF/UA/accessibility validator and human Adobe Reader/assistive-technology review.

See `P12_TAGGED_PDF_CHECKLIST.md`. Automated PDF success must not be cited as PDF/UA conformance, independent pixel fidelity, fillability or human acceptance.
