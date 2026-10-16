# P07 — Files, documents, accessibility and media

- Status: In progress
- Partial implementation commits: `e21c654`, `4e8ee1f`, `c47edf9`

Required gate includes real structured exports, OCR/geometry goldens, accessibility-tree tests and honest YouTube/live-stream limitations.

## Implemented so far

- Local admission policy for txt, CSV, PDF, DOCX, XLSX, PPTX, PNG/JPEG, WAV/MP3 and MP4.
- Canonical paths, existence, per-kind size ceilings, direct reparse-file rejection and signature checks before parser/viewer dispatch.
- Strict HTTPS YouTube watch/short-link policy with exact host matching, credential rejection, video-ID validation and tracking-parameter removal.
- Unit fixtures cover each supported container family plus spoofed extensions, non-HTTPS URLs, lookalike hosts, embedded credentials and non-video YouTube pages.
- Native keyboard-accessible content window now provides path entry plus the Windows file picker, bounded text/CSV display, decoded image view, non-autoplay audio/video with explicit Play/Pause/Stop, bounded folder listing, and validated YouTube handoff into the shared browser.
- PDF and Office containers are admitted but explicitly reported as awaiting structured viewers; they are not falsely presented as editable.

## Current evidence

After the latest increment, the native Release build has 0 warnings/errors and 67/67 tests pass. Native UIA/media E2E also passes:

- A real UTF-8 fixture is rendered through the accessible text value.
- A folder listing exposes three test entries without absolute paths.
- FFmpeg generates a real one-second H.264 MP4; DCx opens it paused and Play/Pause transitions pass.
- A YouTube lookalike host is blocked.
- A valid `youtu.be` URL with tracking data is normalized and handed to a new tab in the shared DCx WebView2 browser.

P07 remains in progress because structured document exports, OCR/geometry and full video-frame Q&A are not yet complete.

## Remaining gate work

- Real structured DOCX/XLSX/PPTX and scanned-PDF wizard exports with semantic validation.
- OCR and geometry goldens plus semantic repair overlays.
- Safe local video pause/resume, frame Q&A and YouTube playback/limitations.
- Audio transcription remains dependent on P06's blocked Whisper backend.
