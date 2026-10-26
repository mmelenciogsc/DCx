# Windows prerequisite bootstrapper validation plan

The bootstrapper is a future separately signed unpackaged WPF utility.  It is not the
current unsigned DCx MSIX and not a proof that any prerequisite is installed.

## Supported v1 actions

| Item | Evidence before launch | Permitted visible handoff |
|---|---|---|
| WebView2 Runtime | Fresh missing detection; selected artifact matches approved hash, architecture, publisher, and product | Vendor's normal interactive installer, with no silent or license-acceptance argument |
| Docker Desktop | Fresh missing detection; selected artifact matches approved hash, architecture, publisher, and product | Docker's normal interactive installer, with no silent or license-acceptance argument |
| WSL 2 | Fresh missing/unsupported feature evidence and an approved Windows configuration | Exact visible `wsl.exe --install --no-distribution` command; no caller arguments or automatic reboot |

Every action also requires separate default-off terms-review and start-installation
acknowledgements for the exact selected item.  Changing detection or a selected artifact
resets both acknowledgements.  A browser handoff to an official terms page is separate
from starting a vendor/Microsoft flow.

## Required tests

1. Pure policy tests: stale/unknown/failed/ready detection, cross-item consent, changed
   artifact, bad hash/publisher/product/architecture/path, silent/agreement flags,
   arbitrary WSL arguments, and success-from-exit inference are all denied.
2. Native UI Automation fixture: guarded launch; keyboard/focus/UIA/live-region checks;
   fake detector/launcher only; zero network, browser, child process, UAC, PowerShell,
   Winget, reboot, download, or installer execution.
3. Clean Windows 11 VM per prerequisite: cancel path, visible terms/elevation where
   supplied by vendor/Microsoft, restart-required state, post-close/reboot re-detection,
   and no automatic DCx launch.
4. Human accessibility: Narrator, NVDA, and JAWS through the bootstrapper itself and
   the transition to vendor/Microsoft-owned dialogs.  UIA does not certify those dialogs.

Do not pass `--silent`, `--quiet`, `--accept-package-agreements`,
`--accept-source-agreements`, `--disable-interactivity`, an automatic reboot option, or
any package-manager command in a production bootstrapper path.
