# UI continuation audit

## Observed architecture

The checked-out product is a WPF/.NET 10 Windows application with a WebView2 surface, native control/focus coordination, bounded browser DOM/ARIA and Windows-broker policies, local private knowledge stores, a signed-global-replica simulation, and an intentionally disabled production lifecycle/finance path. `server/DCx.GlobalService` is a local P09 Kestrel/SQLite simulation, not a deployed customer execution service. The repository uses central package management and lock files; `global.json` pins SDK 10.0.401.

The continuation kit changes the delivery architecture materially: application-managed Debian/Docker services may own UI logic and agentic backends, but the Windows WebView2 host must remain a real semantic interactive viewport. A remote Chromium canvas, VNC/noVNC, cookie copying, a central customer-agent service, or a host-wide broker would violate the requirements.

## Compatibility findings

- Existing P01/P02 WebView2/profile/focus and P03 broker controls are reuse candidates, not proof of the required Docker-backed shared-session design.
- Existing P04 Codex app-server client and P05 BOSS policy are reuse candidates. An official 2026-10-22 check of [Codex app-server with Sign in with ChatGPT](https://developers.openai.com/siwc/token-sharing-open-source/codex-app-server) requires an OAuth access token for the user’s plan and says `model/list` is a catalog rather than an entitlement check; the independent API-key path remains necessary.
- Existing P06 inventory and resource policies are a foundation for U11/U12, but do not demonstrate Docker/WSL GPU passthrough or any physical peripheral workflow.
- Existing P07 document/media, P08 private CKB/CCB, P09 global-sync simulation, P10 contribution, and P11 CARE controls must retain their trust boundaries when surfaced through new UI. No private collection may become a default global upload.
- Existing P13/P14 release controls remain blocked; no installer, signing, deployment, payment, enrollment, or external-provider authority is added by this audit.

## Required decision before UI expansion

U03 is the hard architectural gate. Build one minimally scoped proof in which a Windows WebView2 tab renders the actual user-operable page and exposes its accessibility tree while a Debian-side service can request only authenticated, schema-validated, origin-bound observation/actions through a Windows host bridge. The user must manually log in in that same profile, take control without focus theft, and explicitly resume the agent. If that proof fails, stop UI expansion and revise the architecture through an ADR.

## Initial implementation order

1. Resolve U16's reproducible Ubuntu SDK-10 prerequisite only with approval; add no server/global runtime installation in this branch without a bounded plan.
2. Define U01 semantic contracts and test IDs plus a U03 host-bridge threat model/ADR.
3. Implement the smallest U03 loopback-only origin/IPC proof and its Windows native validator; preserve P01–P05 regression coverage.
4. Add U04/U08 shell controls only after the U03 proof, followed by U05/U06/U07 on narrow contracts.
5. Treat U11–U15, U17, and U18 as separately bounded packets with mock-first tests and explicit physical/manual blockers.

## Risks requiring explicit controls

- Untrusted web content must never invoke host APIs merely through WebView2 messages or prompt content; use an authenticated, versioned, origin allowlisted capability protocol with per-action approval, cancellation, bounds, and audit records.
- WebView2 page semantics and Windows UI Automation must remain primary; browser screenshots/canvas/VNC are not an accessibility fallback.
- User input takes precedence over agent input. Progress is throttled, textual, and non-focus-stealing; pause/take-control/resume/stop must invoke real backend state changes.
- Create App must search approved private/global CKB/CCB first, execute generated artifacts only in constrained owner-approved locations, retain manifest/SBOM/hash/test/rollback evidence, and never treat emitted source text as success.
- SSH evidence is restricted to command-visible facts. Native foreground, login/MFA, device consent, screen reader, and packaged-install acceptance require a live desktop/human record.
