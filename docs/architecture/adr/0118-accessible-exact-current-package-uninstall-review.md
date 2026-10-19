# ADR 0118: expose an accessible exact current-package uninstall review without granting removal authority

- Status: Accepted for local validation; production uninstall remains blocked
- Date: 2026-10-20

## Context

ADR 0117 established a calling-package-only Windows removal adapter, but it had no user-facing review, consequences warning, backup choice, or keyboard/screen-reader evidence. A future production uninstall must bind the user's approval to the exact current package and must not let a reviewed identity diverge from the package Windows removes. Validation must exercise the interaction without installing, removing, closing, elevating, or starting a helper process.

## Decision

1. Canonically bind the operation, typed Windows API, `RemovalOptions.None`, current-user scope, exact package name/full name/publisher/publisher ID/version/architecture, review and expiry UTC times, application-close expectation, noncancellation after API entry, application-data nonpreservation, possible unused-dependency removal, and no elevation into one ten-minute SHA-256 review.
2. Require two separate initially unchecked acknowledgments: uninstall consequences and the user's independent backup choice. Both are required before a one-use request can cross the workflow boundary. Expired, substituted, replayed, canceled, malformed, or untrusted terminal results fail closed with stable content-free codes.
3. Expose a native modeless WPF review with initial heading focus, read-only exact details, assertive status, distinct `Alt+C`, `Alt+B`, `Alt+V`, and `Alt+L` access keys, denial focus handoff, reset acknowledgments, and disabled controls when unavailable.
4. Keep normal product composition unavailable. Unpackaged DCx reports `not_packaged`; even a packaged process reports `production_identity_not_configured` until an approved production identity/signing policy exists. The Windows executor is not composed by the factory.
5. Permit only an explicitly environment-gated generated fixture. It returns `ValidationNoOp` and cannot invoke PackageManager, close DCx, remove data or dependencies, elevate, start a child/helper, or perform a production action.
6. Amend ADR 0117's API seam: the executor passes the reviewed package full name only as an equality constraint. The internal Windows adapter still derives its sole removal target from `Package.Current.Id.FullName`, compares it ordinally with the reviewed value, and denies any mismatch before `RemovePackageAsync`. The caller cannot nominate a different installed package.

## Consequences

DCx now has a user-facing, keyboard/UIA-tested uninstall review and an exact identity-to-executor binding, but no production uninstall authority. Application shutdown/result handoff, signed package identity, backup/recovery policy acceptance, rollback, clean-VM lifecycle, independent assessment, and human JAWS/NVDA/Narrator acceptance remain mandatory. Opening the review never starts removal.

## Validation

Implementation `51475bc` passes 14/14 focused workflow/adapter cases and the full 697/697 Windows test suite with zero build warnings/errors. Exact P13 evidence is `artifacts/test-results/p13-60f5ed88499345538e750e1b7f2c3f98/`: 56/56 reliability, 22/22 package trust, 3/3 WebView2 policy, and native UIA evidence under `package-removal-ui/validation.json`. The fixture gate, heading focus, one exact digest, four distinct access keys, both default-off acknowledgments, keyboard-origin denials, focus handoffs, reset, and one-use `validation_no_op` pass. Normal product composition is unavailable and disabled. PackageManager invocations, child/helper processes, application-close requests, elevation requests, uninstall attempts, and production actions are all zero. Exact P14 evidence is `artifacts/test-results/p14-811c540c71c34175b45993999b4a6759/`; it remains `NO_GO` with seven blockers and activation false.

## Verified upstream contracts

- Microsoft `PackageManager.RemovePackageAsync`: <https://learn.microsoft.com/en-us/uwp/api/windows.management.deployment.packagemanager.removepackageasync?view=winrt-19041>
- Microsoft `RemovalOptions`: <https://learn.microsoft.com/en-us/uwp/api/windows.management.deployment.removaloptions?view=winrt-19041>
- Microsoft `Package.Current`: <https://learn.microsoft.com/en-us/uwp/api/windows.applicationmodel.package.current?view=winrt-19041>
- Microsoft UI Automation overview: <https://learn.microsoft.com/en-us/dotnet/framework/ui-automation/ui-automation-overview>
