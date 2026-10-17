# P07 — Files, documents, accessibility and media

- Status: Complete (automated native gate; human assistive-technology pass unverified)
- Implementation commits: `e21c654`, `4e8ee1f`, `c47edf9`, `c618254`
- Documentation commit: recorded by the commit containing this file

## Implemented

- Bounded local admission for txt, CSV, PDF, DOCX, XLSX, PPTX, PNG/JPEG, WAV/MP3 and MP4, with canonical paths, reparse-file rejection, per-kind limits and binary signature validation.
- Exact-host HTTPS YouTube URL validation, video-ID validation, credential rejection, tracking removal and handoff into the shared DCx WebView2 browser.
- Keyboard/UIA-accessible text, bounded folder, decoded image and non-autoplay audio/video views with explicit Play/Pause/Stop controls.
- Hardened, bounded XML/ZIP text extraction for DOCX, XLSX and PPTX. This is semantic text extraction, not layout-fidelity editing.
- Scanned-PDF wizard using Windows PDF rendering and OCR across all pages, normalized word geometry, ordered blocks and a user-reviewable semantic repair overlay.
- Validated semantic HTML and structured DOCX export. The output is not a tagged accessible PDF and is never presented as editable unless a reviewed block is explicitly marked as a labeled form field.
- Local-video pause, FFmpeg frame extraction, loopback-only Ollama description, continued pause after description and explicit user resume.
- Denial tests for spoofed extensions, malformed containers, YouTube lookalikes, credentials and unsupported pages.

## Reproduction

```bash
cd /mnt/d/DCx
./scripts/verify-p00.sh
powershell.exe -NoProfile -ExecutionPolicy Bypass -File D:\\DCx\\scripts\\validate-p07.ps1
```

The 2026-10-17 full native regression also ran `smoke-desktop.ps1` and `validate-p01.ps1` through `validate-p07.ps1` sequentially and exited 0.

## Measured result

- Locked native Release build: 0 warnings, 0 errors.
- Tests: passed 74, failed 0, skipped 0.
- Real generated H.264 MP4: initial pause and Play/Pause passed.
- Local frame description identified the fixture's solid blue content; video remained paused; explicit user resume passed.
- Real generated WAV Play/Pause and PNG rendering passed.
- YouTube lookalike denial and canonical normalized handoff to a second shared-browser tab passed.
- Windows OCR recognized the golden `DCX`/`42` content and returned normalized geometry.
- Semantic repair, HTML export, real OPC DOCX export and DOCX viewer round-trip passed.
- Native UIA script result: `P07 content viewer UIA validation=PASS`.

## Security, accessibility and limitations

- ZIP/XML parsing caps entry count and expanded bytes; DTD processing and external XML resolution are disabled.
- FFmpeg uses a typed argument list, bounded execution and a private temporary frame that is removed after use. Ollama is loopback-only and the model is unloaded.
- Folder presentation is bounded and does not expose absolute paths in item labels.
- Human JAWS, NVDA and Narrator results remain `UNVERIFIED`; see `docs/accessibility/P07_CONTENT_CHECKLIST.md`.
- YouTube playback/provider limitations are reported in `docs/media/YOUTUBE_LIMITATIONS.md`. The gate proves safe URL admission and handoff, not universal YouTube playback or YouTube frame capture.
- Audio transcription remains blocked by P06's absent, unapproved Whisper backend.
