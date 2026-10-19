# ADR 0117: constrain uninstall to the calling package

- Status: Accepted for an internal uncomposed adapter; API seam amended by ADR 0118; actual uninstall remains blocked
- Date: 2026-10-20

## Context

P13 requires a current-user uninstall lifecycle, but DCx had only an internal update-registration adapter. A future removal boundary must not accept a caller-selected removal target, request all-users removal, imply cancellation after Windows has accepted an irreversible operation, or claim a terminal result without a clean-VM run. ADR 0118 later permits the already reviewed current-package full name only as a target-equality constraint.

Microsoft documents that `PackageManager.RemovePackageAsync` removes a package for the current user, can remove now-unused dependencies, automatically shuts down associated applications and cannot be canceled. `Package.Current` returns the calling app's package only in a packaged process. `RemovalOptions.None` is the zero-valued default and requests none of the data-preservation, defer-while-in-use or all-users flags.

## Decision

1. Add an API seam named `RemoveCurrentPackageAsync`. As amended by ADR 0118, it accepts the exact reviewed package full name only as an equality constraint. The internal Windows adapter obtains its sole target from `Package.Current.Id.FullName`, bounds that OS-derived value, requires ordinal equality with the reviewed value, and passes only the OS-derived value to the two-argument Windows 10 SDK 19041 removal overload. The caller cannot nominate another package.
2. Use only `RemovalOptions.None`. The core closed option policy rejects defer-while-in-use, application-data preservation, roamable-data preservation and all-users removal.
3. Honor caller cancellation only before API entry. The removal API seam deliberately has no cancellation-token parameter because Windows documents the operation as noncancellable after it begins.
4. Await a terminal fake/API result, reject any extended error or still-registered result, redact platform diagnostics and admit only one exact result shape: one PackageManager invocation, current user only, calling package only, Windows application termination expected, no post-entry cancellation, no elevation and no DCx-started installer process.
5. Keep the Desktop adapter internal, unregistered and unreachable from normal startup, UI and controlled-update composition. Automated tests use a fake API. Do not invoke the real Windows removal API against the unsigned validation package.

## Consequences

The codebase now has a compile-checked least-authority uninstall adapter foundation that cannot nominate another installed package or all users. ADR 0118 adds an accessible exact review, separate consequences and backup-choice acknowledgments, and zero-authority validation UI, but still composes no production removal executor. It does not provide a helper/coordinator, result handoff after Windows terminates the app, approved recovery/rollback, or clean-VM acceptance. `RemovalOptions.None` does not preserve application data. Actual signed install/update/uninstall/rollback and human JAWS/NVDA/Narrator acceptance remain P13 blockers.

## Validation

Implementation `d34e930` established the adapter. ADR 0118 milestone `51475bc` adds exact-reviewed-identity equality and the accessible zero-authority workflow. The full Windows suite passes 697/697. Exact P13 evidence is `artifacts/test-results/p13-60f5ed88499345538e750e1b7f2c3f98/`: 56/56 reliability, 22/22 package trust and 3/3 WebView2 policy tests, zero build warnings/errors, clean Defender/NuGet gates and native UIA. Exact P14 evidence is `artifacts/test-results/p14-811c540c71c34175b45993999b4a6759/`; it remains `NO_GO` with seven blockers and activation false. No removal, install, activation, close/relaunch, restart or production action occurred.

## Verified upstream contracts

- Microsoft `PackageManager.RemovePackageAsync`: <https://learn.microsoft.com/en-us/uwp/api/windows.management.deployment.packagemanager.removepackageasync?view=winrt-19041>
- Microsoft `RemovalOptions`: <https://learn.microsoft.com/en-us/uwp/api/windows.management.deployment.removaloptions?view=winrt-19041>
- Microsoft `Package.Current`: <https://learn.microsoft.com/en-us/uwp/api/windows.applicationmodel.package.current?view=winrt-19041>
- Microsoft `PackageId.FullName`: <https://learn.microsoft.com/en-us/uwp/api/windows.applicationmodel.packageid.fullname?view=winrt-19041>
- Microsoft `DeploymentResult`: <https://learn.microsoft.com/en-us/uwp/api/windows.management.deployment.deploymentresult?view=winrt-19041>
