# U03 shared-browser proof plan

Status: design accepted; implementation and native foreground proof pending.

## Existing reusable foundation

| Existing component | Reuse boundary |
|---|---|
| `MainWindow` WebView2 tabs | Remains the live user-visible page; no separate remote renderer. |
| `BrowserControlCoordinator` | Retains explicit approve/take-control/hand-back/expiry state; every bridge action is bound to its lease. |
| `BrowserDomAdapter` | Retains bounded semantic snapshot and password-value denial; no arbitrary script API is exposed to the bridge. |
| Controlled WebView2 fixtures | Provide the only first-proof origin; third-party login is not a fixture substitute. |

## Two bounded packets

### Packet U03-A — typed host bridge contract

Add a core-owned, versioned, strict codec for an origin-bound WebView2 message envelope. Required fields are: protocol version, opaque session identifier, message type, tab identifier, navigation generation, and bounded payload. The host must reject missing/duplicate/unknown fields, strings with controls or over-limit length, wrong origin, stale navigation, wrong tab, expired session, and unexpected type. The page-facing behavior is intentionally limited to fixture readiness, user-input/takeover notification, and proof status; it cannot request host actions.

Tests must cover successful exact-origin delivery plus each rejection case. The existing broad string equality in `OnBrowserWebMessageReceived` must not remain an authority path once this packet is composed.

### Packet U03-B — isolated proof fixture

Add an explicit test-only Docker Compose service with a semantic HTML page and loopback-only port. Its root filesystem is read-only, privileges are dropped, and it has no host, profile, device, model, secret, or customer-data mount. A Windows host test seam creates one per-run opaque bridge session, navigates the existing WebView2 tab to the exact fixture origin, and records only nonsecret proof events.

The fixture must include a labelled text field, a password field that the agent demonstrably cannot read, an approve/take-control/handoff sequence, visible status, and a non-colour state indication. It is not an external login emulator and cannot validate real MFA.

## Evidence required before U04

1. Source SHA, compose/config hash, image digest, port binding, and stop/cleanup result.
2. Codec and lease test results, including hostile message rejection.
3. Native Windows TRX/UIA result for the controlled fixture.
4. A real foreground Windows manual checklist: keyboard focus path, direct page editing, pause/take-control/hand-back, visible user/agent DOM collaboration, and screen-reader observations.
5. Any unavailable desktop, MFA, or assistive-technology gate marked **BLOCKED**, never inferred from SSH.

No U04 shell expansion, credential support, device control, Docker auto-start, third-party login, or production deployment proceeds until these gates are evidenced.
