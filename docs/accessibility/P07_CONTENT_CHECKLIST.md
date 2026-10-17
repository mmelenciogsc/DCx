# P07 content and media accessibility checklist

Automated keyboard/UIA validation passed on 2026-10-17. It exercised local text, folders, image, audio and video controls; optional local-frame questions; shared-viewport web-video pause/visible-rectangle question/continued pause/explicit resume; scanned-PDF OCR and semantic repair; HTML/DOCX export; and YouTube URL handoff. It is not a human screen-reader result, and the web-video fixture is not live YouTube acceptance.

| Assistive technology | Check | Status | Notes |
|---|---|---|---|
| JAWS | Open and navigate local text, folders, images and structured Office text | UNVERIFIED | Human test required |
| JAWS | Operate Play, Pause, Stop and Describe frame; verify focus and live announcements | UNVERIFIED | Human test required |
| JAWS | Ask and follow up about the active web video while it remains paused, then explicitly resume | UNVERIFIED | Human test required; include page-change and unavailable-video errors |
| JAWS | Complete OCR review, semantic repair and export without pointer input | UNVERIFIED | Human test required |
| NVDA | Open and navigate local text, folders, images and structured Office text | UNVERIFIED | Human test required |
| NVDA | Operate Play, Pause, Stop and Describe frame; verify focus and live announcements | UNVERIFIED | Human test required |
| NVDA | Ask and follow up about the active web video while it remains paused, then explicitly resume | UNVERIFIED | Human test required; include page-change and unavailable-video errors |
| NVDA | Complete OCR review, semantic repair and export without pointer input | UNVERIFIED | Human test required |
| Narrator | Open and navigate local text, folders, images and structured Office text | UNVERIFIED | Human test required |
| Narrator | Operate Play, Pause, Stop and Describe frame; verify focus and live announcements | UNVERIFIED | Human test required |
| Narrator | Ask and follow up about the active web video while it remains paused, then explicitly resume | UNVERIFIED | Human test required; include page-change and unavailable-video errors |
| Narrator | Complete OCR review, semantic repair and export without pointer input | UNVERIFIED | Human test required |

Repeat at 200% scaling, High Contrast, keyboard-only operation, media failure, malformed document, unavailable or identity-changed Ollama, interrupted description, no ready video, navigation during a request, protected/denied capture, blocked resume, direct YouTube/provider failure, and inaccessible export destination. Confirm that the answer status is announced, focus stays predictable, the video remains paused for follow-up questions, and explicit resume reports success or a stable failure. Validate that exported semantics are announced in Word and a browser. Do not infer human usability from UIA alone.
