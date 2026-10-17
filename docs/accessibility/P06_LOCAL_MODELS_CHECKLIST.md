# P06 human screen-reader checklist — local models

Status: NOT RUN. Automated UIA evidence does not satisfy this checklist.

Run separately with the installed JAWS, NVDA and Narrator versions. Use only an already-installed nonprivate fixture model; do not download, install, retag or upload anything.

1. Keyboard to **Local models** and confirm the window title, heading, introductory no-download notice and current vision/embedding choices are announced once and in a useful order.
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

P06 remains blocked until this human gate and the separate real Whisper/embedding gates pass.
