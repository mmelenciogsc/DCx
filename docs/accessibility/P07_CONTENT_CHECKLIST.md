# P07 content and media accessibility checklist

Automated keyboard/UIA validation passed on 2026-10-17. It exercised local text, folders, image, audio and video controls; pause/describe/resume; scanned-PDF OCR and semantic repair; HTML/DOCX export; and YouTube URL handoff. It is not a human screen-reader result.

| Assistive technology | Check | Status | Notes |
|---|---|---|---|
| JAWS | Open and navigate local text, folders, images and structured Office text | UNVERIFIED | Human test required |
| JAWS | Operate Play, Pause, Stop and Describe frame; verify focus and live announcements | UNVERIFIED | Human test required |
| JAWS | Complete OCR review, semantic repair and export without pointer input | UNVERIFIED | Human test required |
| NVDA | Open and navigate local text, folders, images and structured Office text | UNVERIFIED | Human test required |
| NVDA | Operate Play, Pause, Stop and Describe frame; verify focus and live announcements | UNVERIFIED | Human test required |
| NVDA | Complete OCR review, semantic repair and export without pointer input | UNVERIFIED | Human test required |
| Narrator | Open and navigate local text, folders, images and structured Office text | UNVERIFIED | Human test required |
| Narrator | Operate Play, Pause, Stop and Describe frame; verify focus and live announcements | UNVERIFIED | Human test required |
| Narrator | Complete OCR review, semantic repair and export without pointer input | UNVERIFIED | Human test required |

Repeat at 200% scaling, High Contrast, keyboard-only operation, media failure, malformed document, unavailable Ollama, interrupted description, and inaccessible export destination. Validate that exported semantics are announced in Word and a browser. Do not infer human usability from UIA alone.
