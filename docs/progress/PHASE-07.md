# P07 — Files, documents, accessibility and media

- Status: In progress
- Partial implementation commits: `e21c654`, `4e8ee1f`

Required gate includes real structured exports, OCR/geometry goldens, accessibility-tree tests and honest YouTube/live-stream limitations.

## Implemented so far

- Local admission policy for txt, CSV, PDF, DOCX, XLSX, PPTX, PNG/JPEG, WAV/MP3 and MP4.
- Canonical paths, existence, per-kind size ceilings, direct reparse-file rejection and signature checks before parser/viewer dispatch.
- Strict HTTPS YouTube watch/short-link policy with exact host matching, credential rejection, video-ID validation and tracking-parameter removal.
- Unit fixtures cover each supported container family plus spoofed extensions, non-HTTPS URLs, lookalike hosts, embedded credentials and non-video YouTube pages.
- Native keyboard-accessible content window now provides path entry plus the Windows file picker, bounded text/CSV display, decoded image view, non-autoplay audio/video with explicit Play/Pause/Stop, bounded folder listing, and validated YouTube handoff into the shared browser.
- PDF and Office containers are admitted but explicitly reported as awaiting structured viewers; they are not falsely presented as editable.

## Current evidence

After the latest increment, the native Release build has 0 warnings/errors and 67/67 tests pass. The viewer compiled successfully, but its native UIA/media E2E has not yet run; P07 remains in progress.

## Remaining gate work

- Native UIA validation for the new folder/text/image/media/YouTube surface.
- Real structured DOCX/XLSX/PPTX and scanned-PDF wizard exports with semantic validation.
- OCR and geometry goldens plus semantic repair overlays.
- Safe local video pause/resume, frame Q&A and YouTube playback/limitations.
- Audio transcription remains dependent on P06's blocked Whisper backend.
