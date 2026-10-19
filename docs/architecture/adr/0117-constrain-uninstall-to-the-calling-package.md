# ADR 0117: constrain uninstall to the calling package

- Status: Accepted for an internal uncomposed adapter; actual uninstall remains blocked
- Date: 2026-10-20

## Context

P13 requires a current-user uninstall lifecycle, but DCx had only an internal update-registration adapter. A future removal boundary must not accept a caller-supplied package full name, request all-users removal, imply cancellation after Windows has accepted an irreversible operation, or claim a terminal result without a clean-VM run.

Microsoft documents that `PackageManager.RemovePackageAsync` removes a package for the current user, can remove now-unused dependencies, automatically shuts down associated applications and cannot be canceled. `Package.Current` returns the calling app's package only in a packaged process. `RemovalOptions.None` is the zero-valued default and requests none of the data-preservation, defer-while-in-use or all-users flags.

## Decision

1. Add an API seam named `RemoveCurrentPackageAsync` with no package-name parameter. The internal Windows adapter obtains only `Package.Current.Id.FullName`, bounds that OS-derived value and passes it to the two-argument Windows 10 SDK 19041 removal overload.
2. Use only `RemovalOptions.None`. The core closed option policy rejects defer-while-in-use, application-data preservation, roamable-data preservation and all-users removal.
3. Honor caller cancellation only before API entry. The removal API seam deliberately has no cancellation-token parameter because Windows documents the operation as noncancellable after it begins.
4. Await a terminal fake/API result, reject any extended error or still-registered result, redact platform diagnostics and admit only one exact result shape: one PackageManager invocation, current user only, calling package only, Windows application termination expected, no post-entry cancellation, no elevation and no DCx-started installer process.
5. Keep the Desktop adapter internal, unregistered and unreachable from normal startup, UI and controlled-update composition. Automated tests use a fake API. Do not invoke the real Windows removal API against the unsigned validation package.

## Consequences

The codebase now has a compile-checked least-authority uninstall adapter foundation that cannot nominate another installed package or all users. It does not provide an uninstall button, user approval, backup warning, helper/coordinator, result handoff after Windows terminates the app, recovery, rollback or clean-VM acceptance. `RemovalOptions.None` does not preserve application data, so a future route must pair explicit accessible consequences with the approved backup/retention policy before authority is granted. Actual signed install/update/uninstall/rollback and human JAWS/NVDA/Narrator acceptance remain P13 blockers.

## Validation

Implementation `d34e930` passes four focused fake-backed removal tests and the full 687/687 Windows suite. Exact P13 evidence is `artifacts/test-results/p13-999c98c9e17d4af1a63a2304fe7563a4/`: 46/46 reliability, 22/22 package trust and 3/3 WebView2 policy tests, zero build warnings/errors, clean Defender/NuGet gates, native UIA and all controlled-update authority counters zero. Exact P14 evidence is `artifacts/test-results/p14-ab8bc9fbe8b64f4fa9378ab7920901f7/`; it remains `NO_GO` with seven blockers and activation false. No removal, install, activation, close/relaunch, restart or production action occurred.

## Verified upstream contracts

- Microsoft `PackageManager.RemovePackageAsync`: <https://learn.microsoft.com/en-us/uwp/api/windows.management.deployment.packagemanager.removepackageasync?view=winrt-19041>
- Microsoft `RemovalOptions`: <https://learn.microsoft.com/en-us/uwp/api/windows.management.deployment.removaloptions?view=winrt-19041>
- Microsoft `Package.Current`: <https://learn.microsoft.com/en-us/uwp/api/windows.applicationmodel.package.current?view=winrt-19041>
- Microsoft `PackageId.FullName`: <https://learn.microsoft.com/en-us/uwp/api/windows.applicationmodel.packageid.fullname?view=winrt-19041>
- Microsoft `DeploymentResult`: <https://learn.microsoft.com/en-us/uwp/api/windows.management.deployment.deploymentresult?view=winrt-19041>
