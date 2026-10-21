# U03-A Core bridge contract evidence

Date: 2026-10-22

Scope: strict parser/validator only; no Docker, WebView2, Windows desktop, credential, device, or browser-profile operation.

## Delivered boundary

`BrowserBridgeMessageValidator` accepts only a UTF-8 JSON v1 envelope from one exact HTTP(S) origin. It binds session ID, tab GUID, positive navigation generation, and a UTC expiry. It rejects all unknown, missing, duplicate, malformed, over-limit, mismatched, expired, or unsupported input with generic rejection codes that do not echo session or payload data.

The accepted messages have fixed inert payloads:

| Type | Payload |
|---|---|
| `fixture-ready` | `{ "ready": true }` |
| `user-input` | `take-control-requested` or `hand-control-back` event only |
| `proof-status` | `ready`, `manual-step-complete`, or `complete` state only |

No accepted payload can request navigation, JavaScript, clipboard/filesystem/device access, host commands, credentials, cookies, DOM content, selectors, URLs, or text input.

## Reproducible validation

Command run from the repository root with the isolated SDK 10 wrapper:

```bash
scripts/dev/use-dotnet-10.sh test tests/DCx.Core.Tests/DCx.Core.Tests.csproj --configuration Release --no-build --no-restore --filter 'FullyQualifiedName~BrowserBridgeMessageValidatorTests|FullyQualifiedName~OpenAiAccessPolicyTests|FullyQualifiedName~SecretRedactorTests' --logger 'console;verbosity=detailed'
```

Observed result: 38 passed, 0 failed, in 7.6193 seconds. This includes 35 U03-A tests plus the pre-existing three-test portable access/redaction subset (the test runner expands theory cases, so the selected-case count is 38).

Focused U03-A rerun after the final boundary additions:

```bash
scripts/dev/use-dotnet-10.sh test tests/DCx.Core.Tests/DCx.Core.Tests.csproj --configuration Release --no-restore --filter FullyQualifiedName~BrowserBridgeMessageValidatorTests --logger 'console;verbosity=detailed'
```

Observed result: 35 passed, 0 failed, in 0.9720 seconds.

`git diff --check` passed for the working change set.

## Windows Core confirmation

The approved Windows checkout was cleanly fast-forwarded over restricted SSH to source SHA `8138265da7696705a4015c9acb67059085b415a0`. On that host, SDK 10.0.402 built `tests/DCx.Core.Tests/DCx.Core.Tests.csproj` in Release with 0 warnings and 0 errors. The following focused command then passed 35/35 in 0.6800 seconds:

```powershell
dotnet test tests\DCx.Core.Tests\DCx.Core.Tests.csproj --configuration Release --no-build --no-restore --filter 'FullyQualifiedName~BrowserBridgeMessageValidatorTests' --logger 'console;verbosity=normal'
```

The first SSH attempt occurred before rebuilding the test project and used an incorrectly escaped PowerShell exit-status variable; it reported no matching tests and is not treated as a validation result. Rebuild followed by the command above is the recorded result. Restricted SSH still cannot prove a logged-in desktop, foreground UI, browser source event, user consent, or screen-reader behavior.

## Independent review

The first independent review found arbitrary per-type payloads and no expiry binding. Those P1 findings were repaired and re-audited: P0=0, P1=0. The re-audit also requested isolated string and boundary tests; those were added before the focused 35/35 rerun.

## Remaining gates

This is not a shared-browser proof. U03-B must compose this validator with the existing `BrowserControlCoordinator` revocation path, run an explicit loopback-only Docker fixture, and remove any legacy WebView2 message authority path. A real foreground Windows WebView2 session, keyboard-only path, visible user/agent interaction, and Narrator/NVDA/JAWS observations remain required manual gates. SSH, HTTP checks, screenshots, VNC/noVNC, and this unit-test result do not satisfy them.
