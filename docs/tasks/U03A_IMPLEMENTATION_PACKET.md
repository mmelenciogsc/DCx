# U03-A implementation packet

Implement only the strict Core contract described in [the U03 plan](../U03_SHARED_BROWSER_PROOF_PLAN.md).

Scope is limited to `src/DCx.Core/Control/` and `tests/DCx.Core.Tests/Control/`. Do not change Desktop, Docker, server, profiles, credentials, device, deployment, or documentation. Do not run containers, Windows actions, network actions, or publish, commit, or push.

Create an origin/session-bound page-to-host JSON validator using existing `BrowserOrigin.FromUri(Uri)`. It needs a small success DTO exposing only type and payload; exact required JSON fields (`version`, `sessionId`, `type`, `tabId`, `navigationGeneration`, `payload`); allowlisted types; UTF-8 byte limits; duplicate/unknown/missing/type/binding/control-character rejection; and safe non-echoing errors. Constructor accepts approved URI, session ID, tab `Guid`, and generation. Source must be exact-origin checked. Add deterministic xUnit tests, including a duplicate-key rejection and a lookalike origin.

Do not redefine `BrowserOrigin`, grant arbitrary host action, or emit session/payload values in rejection messages. Report changed files and exact focused test output. Do not claim U03 browser or manual validation.
