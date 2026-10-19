# P07 — Files, documents, accessibility and media

- Status: Complete (automated native gate; human assistive-technology pass unverified)
- Implementation commits: `e21c654`, `4e8ee1f`, `c47edf9`, `c618254`, `10e07d5`, `ed6f001`, `acc8e62`, `040fb4e`, `1a3b28e`, `15a6061`, `73c154d`, `ac6debc`, `c12606d`, `a7cbd89`, `5873218`; validator hardening `02d0564`; shared local-inference sequencing `2713413`; live resource admission and verified cleanup `94a0f6b`; explicit DXGI inventory/admission boundary `3435450`; shared-runtime quiet fence `e66dc3a`; owned-runtime foundation `69aabc0`; protected production host `db448a9`; atomic semantic exports `5931485`; exact Windows/WSL evidence binding `c3360af`; strict URL/tagged flattened PDF `3b35701`; corrected readiness boundary `287660b`; digest-bound aspect-preserved source raster `1528e60`
- Documentation commit: recorded by the commit containing this file

## Implemented

- Bounded local admission for txt, CSV, PDF, DOCX, XLSX, PPTX, PNG/JPEG, WAV/MP3 and MP4, with canonical paths, reparse-file rejection, per-kind limits and binary signature validation.
- Exact-host HTTPS YouTube URL validation, video-ID validation, credential rejection, tracking removal and handoff into the shared DCx WebView2 browser.
- Keyboard/UIA-accessible text, bounded folder, decoded image and non-autoplay audio/video views with explicit Play/Pause/Stop controls.
- Hardened, bounded XML/ZIP text extraction for DOCX, XLSX and PPTX. This is semantic text extraction, not layout-fidelity editing.
- Scanned-PDF wizard using Windows PDF rendering and OCR across all pages, normalized word geometry, ordered blocks and a user-reviewable semantic repair overlay.
- Validated semantic HTML and structured DOCX export with bounded XML-safe inputs, propagated OCR language, defined title/outline-heading/caption/table styles, core metadata, internal-only relationships and atomic same-directory replacement. Repeated export replaces a prior artifact only after the new file is complete.
- Strict public-HTTPS scanned-PDF URL intake with redirect/cookie/proxy/ambient-credential denial, public-address-only connection pinning, exact media/signature/size bounds and operation-private cleanup. A literal-loopback origin exists only under an exact explicit fixture gate.
- Tagged flattened PDF export through CSP-locked local semantic HTML and a private WebView2 print profile. The exact OCR-reviewed source digest is reverified under the render read lease; each source page becomes a bounded aspect-preserved raster and reviewed text coordinates are transformed into the same letterboxed placement. Atomic admission requires PDF markers, `/StructTreeRoot` and `/MarkInfo`; test-only inspection proves raster samples/placement/aspect, extracted words and normalized coordinates. The output is non-editable and is not claimed as PDF/UA-conformant, independently pixel-validated or human accepted.
- Local-video pause, frame extraction through the exact approved FFmpeg executable/license binding, bounded optional question, protected app-owned loopback Ollama description, continued pause after description and explicit user resume. No FFmpeg executable is selected from `PATH`.
- User-invoked shared-viewport web-video Q&A: the current tab's largest ready, visible top-document HTML5 video is paused, its viewport-clipped rectangle is captured, the bounded question and frame go only to the same protected app-owned vision host, playback remains paused for follow-up questions, and resume is a separate same-tab/page action. The host reuses only the exact digest-bound model for bounded follow-ups and dies with the app.
- Admitted WAV transcription through the exact approved local Whisper binding plus admitted MP3/MP4 transcription through separately approved FFmpeg extraction and Whisper recognition. The viewer pauses, announces extraction and recognition, exposes accessible cancel throughout, bounds strict transcript text, focuses completion and denies immediately when either required binding is cleared.
- Denial tests for spoofed extensions, malformed containers, YouTube lookalikes, credentials and unsupported pages.

## Reproduction

```bash
cd /mnt/d/DCx
./scripts/verify-p00.sh
pwsh.exe -NoProfile -ExecutionPolicy Bypass -File D:\\DCx\\scripts\\validate-p07.ps1
```

The 2026-10-17 full native regression also ran `smoke-desktop.ps1` and `validate-p01.ps1` through `validate-p07.ps1` sequentially and exited 0.

## Measured result

- Locked native Release build: 0 warnings, 0 errors.
- Original P07 content tests: passed 74, failed 0, skipped 0. Bounded-question policy tests: passed 5, failed 0, skipped 0. Focused accessible-document and strict-downloader cases: passed 23, failed 0, skipped 0. Current full native suite: passed 641, failed 0, skipped 0.
- Real generated H.264 MP4: initial pause and Play/Pause passed.
- Local frame description identified the fixture's solid blue content in 135,295 ms on the cold owned runtime; video remained paused; explicit user resume passed.
- Shared-viewport generated HTML5 video Q&A answered `Blue` in 2,187 ms through the same resident exact model; the video remained paused after narration and explicit same-page resume passed.
- Real generated WAV Play/Pause and PNG rendering passed.
- YouTube lookalike denial and canonical normalized handoff to a second shared-browser tab passed.
- Windows OCR recognized the golden `DCX`/`42` content and returned normalized geometry.
- Semantic repair, atomic HTML export, real OPC DOCX replacement, explicit title/heading/language semantics and DOCX viewer round-trip passed. Strict URL download, tagged flattened PDF atomic replacement, structure/marked-content markers, extracted `DCX ACCESSIBLE 42` text, reviewed normalized coordinates and private-source cleanup passed. Invalid XML, missing geometry, canceled export, unsafe URLs and mismatched extensions fail closed.
- Native UIA script result: `P07 content viewer UIA validation=PASS`.
- Latest passing evidence directory: `artifacts/test-results/p07-b23ffd5203724e8bad1fd443578d271c/` (generated from exact clean source `1528e60` and intentionally not committed). Its `p07-evidence.json` binds the full commit and clean tracked state; `flattened-pdf-inspection.json` records one page, three words, one 2550-by-1020 aspect-preserved raster and transformed normalized coordinates. The disposable profile explicitly reviewed/approved the signed 63-file CUDA-v13 runtime, selected the exact hardware-policy-chosen 4B vision identity, registered the real installed FFmpeg candidate and proved both cold local and warm web-video paths. It then closed DCx, proved the app-owned Ollama process/listener stopped, removed the private downloaded source and preserved the pre-existing service.
- One earlier committed-source run transiently failed to observe the current vision-model UIA name after the persisted-save status. No orphan DCx process remained; the immediate rerun and a later independent rerun both passed. The passing runs are the accepted gate evidence, while the isolated timing observation remains documented rather than being represented as a zero-flake claim.
- The first P12 aggregate at this milestone stopped when the P07 validator dereferenced a UIA lookup before the control appeared. An immediate unmodified standalone retry passed, identifying validator timing rather than a product assertion. The first wait-hardening attempt then looked for conditional media controls before opening video and correctly failed `media Play action not exposed`. Commit `02d0564` moved that wait after video admission and applies bounded `Require-ById` waits to required controls. The final standalone gate and the complete exact-source P12 aggregate both passed. Neither failed run is represented as product acceptance.
- During `1528e60` development, the first raster-enabled run passed PDF raster/tag/text/coordinate assertions but then failed because a WinRT source-file hold prevented private URL-temporary deletion. OCR and rendering now use a deterministic delete-sharing managed read stream; the next complete PDF run and exact clean aggregate passed cleanup. One intervening unchanged run stopped earlier when local vision failed the existing solid-blue content assertion; it never reached the PDF path and is not represented as PDF evidence. Final exact clean evidence is the aggregate path above.

## Security, accessibility and limitations

- ZIP/XML parsing caps entry count and expanded bytes; DTD processing and external XML resolution are disabled.
- Document graphs are bounded before export; invalid XML characters/language and missing reviewed geometry fail closed. HTML encodes text/attributes. DOCX defines its referenced styles and metadata and emits no external relationship. PDF generation admits only a bounded single-field DevTools response and requires structure markers before atomic replacement. Random operation/same-directory temporaries are deleted best-effort; immutable managed strings, secure erase and malicious same-user destination-directory isolation are not claimed. See ADRs 0097/0099 and the two scanned-document/PDF accessibility checklists.
- FFmpeg requires a separately approved exact executable/license binding, with per-run digest verification, leased input/executable, typed arguments, a scrubbed environment, bounded execution and signature-validated leased output removed after use. Web capture is restricted to the visible video rectangle in the current tab. Ollama runs in the exact protected signed app-owned process; approval is re-read per operation, exact model/license identity is revalidated before and after inference, cold resource admission is enforced, only the exact request name or digest-derived runtime alias may reside, decoded frame bytes are zeroed, and failure/revocation/app exit kills the whole owned job. Loopback lacks client authentication, so malicious same-user isolation remains blocked.
- Folder presentation is bounded and does not expose absolute paths in item labels.
- Human JAWS, NVDA and Narrator results remain `UNVERIFIED`; see `docs/accessibility/P07_CONTENT_CHECKLIST.md`.
- YouTube playback/provider limitations are reported in `docs/media/YOUTUBE_LIMITATIONS.md`. The gate proves safe URL admission/handoff and provider-neutral Q&A against a deterministic loopback HTML5 video. It does not prove live, authenticated, embedded, protected or universal YouTube frame Q&A.
- The fake-CLI native WAV/MP3 flow proves accessible broker composition, cancellation and text presentation, not speech or extraction quality. A real approved Whisper backend and accuracy/resource evidence remain blocked by P06.
- The real unsigned FFmpeg candidate was approved only in a disposable automated profile to prove wiring; it remains unapproved for normal use and requires owner/legal/provenance review. Human JAWS/NVDA/Narrator validation of the expanded media controls remains unverified.
- The automated flattened branch of P12's URL-to-inaccessible-PDF-to-saved-PDF scenario is implemented with an embedded aspect-preserved raster. It must not be cited as independent pixel-level visual comparison, fillable AcroForm support, PDF/UA conformance or Adobe Reader/JAWS/NVDA/Narrator acceptance.
