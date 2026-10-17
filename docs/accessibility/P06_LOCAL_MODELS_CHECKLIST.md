# P06 human screen-reader checklist — local models

Status: NOT RUN. Automated UIA evidence does not satisfy this checklist.

Run separately with the installed JAWS, NVDA and Narrator versions. Use only an already-installed nonprivate fixture model; do not download, install, retag or upload anything.

1. Keyboard to **Local models** and confirm the window title, heading, introductory no-download notice and current vision/embedding/transcription choices are announced once and in a useful order.
2. Confirm **Refresh installed models**, the identity list, complete license field, approval checkbox, role actions, clear actions and **Close** are reachable with visible focus.
3. Select a mutable/duplicate identity and confirm its exact name/digest, size, capabilities, license summary and ineligibility are understandable. Confirm neither role action permits saving it.
4. Select an eligible digest-qualified identity. Read the complete license field with normal screen-reader review commands. Confirm the exact model and license digests can be reviewed without character loss.
5. Confirm approval starts unchecked. Invoke a role action before approval and verify a concise status directs focus to approval without changing the selection.
6. Check approval, save vision, and confirm the status and current-selection text identify the exact saved binding. Confirm approval resets unchecked.
7. Repeat for an eligible embedding model if an approved one exists; otherwise record the unavailable control/state without installing one.
8. Clear each role independently and confirm the model itself is not deleted or changed.
9. Stop Ollama, refresh, and confirm the failure is announced without exception text, stale approval or an automatic remote fallback. Restart only the pre-existing service after the check.
10. Open a local video with no vision choice, invoke frame description and confirm playback stays paused while the app explains how to select a model. With the approved vision binding, confirm processing, result and manual resume are usable.
11. Repeat at 200% scaling, Windows High Contrast and keyboard-only operation. Record screen reader/app/Windows versions, failures, unexpected verbosity, focus loss and workarounds.
12. In the already-installed Whisper group, confirm executable, model and two license path fields plus all browse/review/save/clear actions are keyboard reachable with distinct access keys and useful labels. Do not select or install an unapproved backend.
13. With the deterministic fake CLI fixture only, confirm approval begins unchecked and disabled until review. Review the four files; read both complete license texts, all four digests, sizes and the warning that identity/accuracy are not established.
14. Invoke save before approval and confirm the live status refuses it and moves focus to approval. Approve and save; confirm approval resets, the current transcription binding is announced, and no download/install action exists.
15. Change any reviewed path and confirm approval/review are invalidated. Clear registration and confirm every selected executable/model/license file remains on disk.
16. With an approved real backend, open an admitted WAV in **Files and media**. Confirm **Transcribe media** pauses playback, announces processing, keeps **Cancel transcription** available, and focuses the completed read-only transcript without reading process diagnostics.
17. Cancel a long transcription and confirm no partial text remains. Clear the Whisper registration and invoke transcription again; confirm the unavailable guidance is announced and stale transcript text is absent.
18. In the FFmpeg group, review an approved fixture executable and complete license. Confirm exact digests, size, real Windows signature status and the publisher/legal warning are announced; approval begins unchecked and save-before-approval fails closed.
19. Save and clear the fixture binding. Confirm approval resets, the current media-processor identity/status is announced, source files remain, and no download/install/bundle action exists. Do not approve the real unsigned host candidate without owner/legal review.
20. With separately approved real Whisper and FFmpeg bindings, repeat **Transcribe media** for admitted MP3 and MP4 fixtures. Confirm extraction and transcription are separately announced, cancellation is available throughout, the transcript receives focus, and the media remains paused. Clear FFmpeg while retaining Whisper and confirm MP3/MP4 fail with concise registration guidance while direct WAV remains available.

P06 remains blocked until this human gate and the separate real Whisper/embedding gates pass.
