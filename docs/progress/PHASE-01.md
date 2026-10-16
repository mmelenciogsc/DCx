# P01 — Native shell and accessible browser

- Status: Complete (automated native-Windows gate; human screen-reader rows unverified)
- Commit: `d12fc64`

The P00 shell is feasibility code only. P01 must implement tabs, history, downloads, persistent isolated profiles, keyboard/focus behavior and agent drawer, then prove a real HTTPS interaction with native UIA tests and separately recorded JAWS/NVDA/Narrator checklists.

## Implementation plan and assumptions

1. Keep WPF/WebView2 behind a small tab/profile coordinator; every profile uses its own DCx local-data directory and never imports another browser session.
2. Add keyboard-first tabs, address navigation, history, bookmarks, downloads, profile choice, status announcements and an accessible agent drawer whose unavailable P02 actions are clearly disabled.
3. Persist only browser-management metadata needed by the feature; use atomic local writes and reject non-HTTP(S) navigation from DCx controls.
4. Validate domain policies and stores with unit tests, then launch the native executable, load a real HTTPS fixture (`https://example.com`), and inspect/operate the shell through Windows UI Automation.
5. Record a manual JAWS/NVDA/Narrator checklist with every unperformed item marked unverified. Automated UIA evidence must not satisfy those rows.

Assumptions: network access to `example.com` is test-only and contains no user data; download tests use a controlled local fixture rather than arbitrary internet content; P02 owns actual agent input/handoff behavior.

## Implemented

- Multi-tab WPF/WebView2 browser with Back, Forward, Reload/Stop, address navigation and new/close/next-tab commands.
- Default, Work and Private profile choices; every profile maps to a distinct DCx-owned persistent WebView2 directory. No external browser data is imported.
- Atomic local JSON history and bookmarks with HTTP(S)-only validation and bounded entry counts.
- Downloads saved without automatic execution under the user's `Downloads\DCx` directory; filename traversal is removed and collisions receive numeric suffixes.
- Accessible bookmarks/history/download panels and a semantic Agent-control group. Agent execution remains visibly disabled until P02.
- Common browser keyboard shortcuts documented in `docs/user/KEYBOARD_SHORTCUTS.md`; no global hotkeys.
- Controlled accessible web fixture with a labeled manual-control field and attachment endpoint.
- Isolated test roots through `DCX_DATA_ROOT` and `DCX_DOWNLOAD_ROOT`; production defaults remain the Windows user profile.

## Reproduction

```bash
cd /mnt/d/DCx
./scripts/verify-p00.sh
powershell.exe -NoProfile -ExecutionPolicy Bypass -File D:\\DCx\\scripts\\smoke-desktop.ps1
powershell.exe -NoProfile -ExecutionPolicy Bypass -File D:\\DCx\\scripts\\validate-p01.ps1
```

## Measured final results

Native Windows 11 build 26200.9457, .NET SDK 10.0.401:

- Locked restore: passed for four projects.
- Release build: passed, 0 warnings, 0 errors, 0.75 seconds.
- Unit tests: passed 21, failed 0, skipped 0, 88 ms.
- General desktop smoke: title updated to `Example Domain - DCx / Agentic Browser`; required UIA elements 5/5.
- P01 UIA E2E: passed in 7.9 seconds.
  - Real HTTPS page: `https://example.com/`.
  - Browser tabs: 2 accessible profile-named selectors; only the selected WebView correctly appears in the live UIA tree.
  - Physical isolated profiles: Default and Work confirmed in an isolated test data root.
  - Keyboard: `Ctrl+L`, `Ctrl+T`, `Ctrl+W` exercised.
  - Live-page field: labeled `Approval code` field found and edited through the WebView UIA tree without submitting data.
  - Accessible collections: 1 bookmark, at least 1 history entry, 1 controlled download.
  - Controlled downloaded artifact: `dcx-fixture.txt` under a unique repository test-results directory.
  - Agent drawer: semantic `Agent controls` group exposed and closeable.

## Security/accessibility observations

- Browser-controlled navigation and metadata accept HTTP(S) only. Popup requests use the same policy and open as same-profile tabs.
- Download filenames cannot choose parent paths; downloads never auto-open.
- The app remains `asInvoker`; WebView2 DevTools are disabled in this build.
- Status text is a polite live region; toolbar, profiles, tabs, selected WebView, collections and Agent group expose stable names/roles.
- WPF correctly excludes inactive tab content from the accessibility tree while retaining accessible tab selectors.
- No credential or login submission occurred. The fixture field contains a non-secret test value only.
- The human checklist in `docs/accessibility/P01_SCREEN_READER_CHECKLIST.md` marks every JAWS/NVDA/Narrator row UNVERIFIED. No human assistive-technology pass is claimed.

## Changed files

- Browser shell: `src/DCx.Desktop/MainWindow.xaml*`.
- Browser policies/stores: `src/DCx.Core/Browsing`, `Downloads`, `Profiles`, `Storage`.
- Deterministic tests: new Core test groups plus `tests/DCx.WebFixtureServer`.
- Automation: `scripts/smoke-desktop.ps1`, `scripts/validate-p01.ps1`.
- User/accessibility docs: keyboard shortcuts and screen-reader checklist.

## Known limitations carried forward

- Human keyboard/speech/braille behavior with JAWS, NVDA and Narrator is unverified and remains release-blocking.
- P01 does not automate or submit an external login. Authenticated handoff belongs to P02/P12 and must use the DCx WebView2 profile.
- Downloads have safe paths and no auto-open, but malware/content scanning and approval workflows are later security phases.
- No hosted CI run exists because no Git remote is configured; the identical commands passed locally on Windows.
