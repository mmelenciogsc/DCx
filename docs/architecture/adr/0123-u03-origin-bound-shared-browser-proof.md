# ADR 0123: Prove one origin-bound shared WebView2 session before continuation UI expansion

- Status: Accepted for U03 design and bounded implementation.
- Date: 2026-10-22
- Source: continuation requirement for a real semantic shared browser, not a screenshot/VNC surface.

## Decision

Retain the native Windows WPF/WebView2 viewport as the only user-visible browser surface. A future Debian/Docker service may provide a narrowly scoped local test page and agent-side session service, but it never renders a remote desktop or receives a browser profile, password, cookie, private CKB/CCB, or arbitrary host command.

U03 is complete only when one exact test origin is visibly loaded in that WebView2 instance and the following proof is retained:

1. The user can focus, type into, and activate controls in the live semantic page using keyboard alone.
2. The user explicitly approves a scoped browser-control request, then can take control, complete a manual-login/MFA/consent-like fixture interaction, and explicitly hand control back.
3. The agent receives only an allowlisted, bounded DOM snapshot/action request through the existing `BrowserControlCoordinator` lease and `BrowserDomAdapter`; it never receives password values, cookies, or storage.
4. The Windows host accepts a page-to-host message only after validating the exact source origin, per-navigation opaque session identifier, schema version, message type, size, tab, and navigation generation. Any navigation, physical input, malformed message, unexpected origin, expiry, stop, or takeover revokes the lease.
5. Container/service exposure is loopback-only and operates with read-only root filesystem, no privilege escalation, no host network, no browser-profile mount, no broad host mount, no device mount, and no secret-bearing environment value. Fixture content and image digests are checksum-bound in the evidence record.

The initial proof origin must be a controlled local fixture. A live third-party login remains manual, same-session, and separately approved; its successful use cannot be inferred from SSH or automation output.

## Rationale

`MainWindow` already hosts the live WebView2 tab, and the existing coordinator binds action leases to tab, origin, generation, and expiry. The existing `OnBrowserWebMessageReceived` string marker is insufficient for a Docker-backed host bridge because it does not validate a message source, session proof, or typed schema. A narrow bridge must therefore strengthen this boundary before adding tabs, omnibox features, or an agent UI redesign.

## U03 implementation boundary

The first code packet must add a versioned strict message contract and tests for all rejection paths before attaching a container. It may admit only a fixed controlled origin and the `user-input`/proof lifecycle messages needed by the harness. It must not add arbitrary JavaScript execution, clipboard/filesystem/device access, credential transfer, terminal routing, public listening ports, model downloads, or container auto-start.

The second packet may add an isolated Docker Compose fixture and Windows host-launch seam only after the contract tests pass. It must be started explicitly by the test harness and stopped afterward. Ubuntu may lint/configure the compose file and run a disposable Linux service test, but Windows must prove the actual WebView2 session.

## Validation and blocked gates

Automated gates: strict codec/origin/session/generation tests; lease revocation tests; compose syntax/security checks; loopback-only port probe; container digest and artifact checksum capture; and native WPF/WebView2 fixture tests.

Manual Windows gates: foreground WebView2 focus sequence; keyboard-only takeover and return; visible semantic DOM/user/agent collaboration; manual login/MFA/consent simulation; and Narrator/NVDA/JAWS observations. SSH, headless browser, screenshot, VNC/noVNC, and a Docker HTTP response do not satisfy these gates.
