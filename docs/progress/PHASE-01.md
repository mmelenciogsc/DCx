# P01 — Native shell and accessible browser

- Status: In progress
- Commands/results: none recorded
- Changed files: none attributable to P01
- Commit: none

The P00 shell is feasibility code only. P01 must implement tabs, history, downloads, persistent isolated profiles, keyboard/focus behavior and agent drawer, then prove a real HTTPS interaction with native UIA tests and separately recorded JAWS/NVDA/Narrator checklists.

## Implementation plan and assumptions

1. Keep WPF/WebView2 behind a small tab/profile coordinator; every profile uses its own DCx local-data directory and never imports another browser session.
2. Add keyboard-first tabs, address navigation, history, bookmarks, downloads, profile choice, status announcements and an accessible agent drawer whose unavailable P02 actions are clearly disabled.
3. Persist only browser-management metadata needed by the feature; use atomic local writes and reject non-HTTP(S) navigation from DCx controls.
4. Validate domain policies and stores with unit tests, then launch the native executable, load a real HTTPS fixture (`https://example.com`), and inspect/operate the shell through Windows UI Automation.
5. Record a manual JAWS/NVDA/Narrator checklist with every unperformed item marked unverified. Automated UIA evidence must not satisfy those rows.

Assumptions: network access to `example.com` is test-only and contains no user data; download tests use a controlled local fixture rather than arbitrary internet content; P02 owns actual agent input/handoff behavior.
