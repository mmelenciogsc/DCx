# ADR 0103: Hand off the WebView2 prerequisite without installer authority

- Status: Accepted for accessible prerequisite recovery; installation acceptance remains blocked
- Date: 2026-10-19

## Context

The self-contained DCx payload deliberately excludes Microsoft Edge WebView2 Runtime. Stable/Evergreen detection already failed closed when the Runtime was missing, but copying a URL left a keyboard and screen-reader user to reconstruct the recovery workflow. Microsoft's WebView2 distribution guidance permits an application to direct users to the official consumer download page as an alternative to programmatic deployment. Downloading or invoking Microsoft's bootstrapper would add network, installer and possible elevation authority that has not been approved or clean-VM validated.

## Decision

Keep WebView2 external and Evergreen. When a fresh local probe proves it missing, expose the exact query-free HTTPS address `https://developer.microsoft.com/microsoft-edge/webview2/consumer/`. Copying remains inert. Opening the page requires a separate initially unchecked approval, is single-use, and hands only that constant address to the operating system's default browser.

The policy grants no download, installer-execution or elevation authority. DCx does not make an HTTP request, select an installer, start the installer, decide per-user versus per-machine scope, accept terms, monitor installation or bypass browser security. After the user independently completes or declines Microsoft's flow, they return to DCx and explicitly recheck the Runtime. A newly available Runtime does not cause the page to open.

Production and validation adapters are separate. The native missing-runtime fixture injects a no-op adapter and asserts the approval reset, distinct access keys, exact accessible status, zero child processes, zero profile creation, zero downloads and zero installs. Core policy tests bind the exact origin/path and prove that permitted handoff still grants no installer/elevation authority.

## Consequences

- Keyboard and screen-reader users receive a short, explicit prerequisite recovery workflow without copying into another browser manually.
- The default browser may contact Microsoft and use its own profile, cookies, proxy and enterprise policies. That external browser activity is outside DCx and occurs only after the user's one-use approval.
- An existing machine-wide Edge Updater can affect installation scope; DCx neither predicts nor controls that behavior.
- This closes only the product-side page handoff. Actual download/install, per-user and machine-wide outcomes, enterprise policy, clean-VM lifecycle, licensing/terms acceptance and human JAWS/NVDA/Narrator acceptance remain blocked.
