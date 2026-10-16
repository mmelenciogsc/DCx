# P07 — Files, documents, accessibility and media

- Status: In progress
- Partial implementation commit: `e21c654`

Required gate includes real structured exports, OCR/geometry goldens, accessibility-tree tests and honest YouTube/live-stream limitations.

## Implemented so far

- Local admission policy for txt, CSV, PDF, DOCX, XLSX, PPTX, PNG/JPEG, WAV/MP3 and MP4.
- Canonical paths, existence, per-kind size ceilings, direct reparse-file rejection and signature checks before parser/viewer dispatch.
- Strict HTTPS YouTube watch/short-link policy with exact host matching, credential rejection, video-ID validation and tracking-parameter removal.
- Unit fixtures cover each supported container family plus spoofed extensions, non-HTTPS URLs, lookalike hosts, embedded credentials and non-video YouTube pages.

## Current evidence

After this increment, the native Release build has 0 warnings/errors and 67/67 tests pass. This is only the hostile-input admission boundary; P07 remains in progress.

## Remaining gate work

- Native folder browser and accessible viewers.
- Real structured DOCX/XLSX/PPTX and scanned-PDF wizard exports with semantic validation.
- OCR and geometry goldens plus semantic repair overlays.
- Safe local video pause/resume, frame Q&A and YouTube playback/limitations.
- Audio transcription remains dependent on P06's blocked Whisper backend.
