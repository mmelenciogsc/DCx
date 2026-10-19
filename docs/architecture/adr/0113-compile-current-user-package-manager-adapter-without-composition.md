# ADR 0113: compile a current-user PackageManager adapter without composing activation

- Status: Accepted for an uncomposed adapter; production execution remains blocked
- Date: 2026-10-19

## Context

ADR 0112 holds the exact staged manifest and package against write, delete and rename while it revalidates before and after a product-owned executor. That boundary still needed a concrete Windows adapter whose contract matches the Windows 10 SDK projection used by DCx, without making the adapter reachable from normal product composition or invoking it against the unsigned validation package.

The newer `AddPackageByUriAsync`/`AddPackageOptions` surface is documented against newer Windows SDK projections. Its `LimitToExistingPackages` option is unavailable in DCx's Windows 10 build 19041 projection and concerns missing referenced-package acquisition rather than a safe local-only deployment guarantee. Raising the minimum SDK or weakening the option policy solely to reach that API is not justified.

## Decision

Compile an internal Windows adapter around `PackageManager.AddPackageAsync(localPackageUri, emptyDependencyUris, DeploymentOptions.None)`. This API is current-user scoped in the official Windows 10 SDK 19041 contract. The adapter supplies one canonical local file URI, no dependency package URIs and the zero-valued `None` option, so it requests none of the shutdown, development, resource-expansion, downgrade, retain-on-failure or stage-in-place behaviors represented by `DeploymentOptions`.

The outer lease-held transaction rehashes both files before and after execution. Before API entry, the core executor independently validates the transaction context's exact non-reparse `.msix` path, byte length, canonical digest fields and four-part version. It accepts only the closed safe option object, waits for the Windows operation to reach a terminal result, requires no thrown operation failure or extended error, and requires `IsRegistered=true`. Cancellation is propagated. Platform diagnostics and exception text are never returned or logged; failures map to stable product codes. The typed transaction result records only registration status and zero elevation, restart and DCx-started installer-process counts.

The desktop adapter is internal and deliberately has no dependency-injection registration, startup route, command or UI. Normal DCx continues to use `not_configured`; automated validation exercises a fake deployment API only. The real Windows package API was not invoked.

## Consequences

The repository now contains a compile-checked Windows implementation for a future owner-approved activation route while retaining zero activation authority. A signed-positive production package, protected production origin/root/identity, a one-use accessible activation UI, application-close and restart coordination, rollback/uninstall behavior, clean-VM lifecycle testing, independent assessment and human JAWS/NVDA/Narrator acceptance remain P13 blockers.

## Validation

Implementation `fc23b48` passes 24/24 focused controlled-update/adapter tests, 42/42 P13 reliability tests and the full 682/682 Windows suite with zero build warnings/errors. Exact P13 evidence is `artifacts/test-results/p13-e0abd97bf39849e792dec2f561fa1b37/`. The gate recorded zero controlled-update network requests, installer processes, elevation requests, activation attempts, restart attempts and production actions. Exact P12 review evidence `artifacts/test-results/p12-package-approval-d582b16ecdf9444183027b7dc0271c5b/` passes 12/12 plus native UIA with zero installer child processes.

## Verified upstream contracts

- Microsoft `PackageManager.AddPackageAsync`: <https://learn.microsoft.com/en-us/uwp/api/windows.management.deployment.packagemanager.addpackageasync?view=winrt-19041>
- Microsoft `DeploymentOptions`: <https://learn.microsoft.com/en-us/uwp/api/windows.management.deployment.deploymentoptions?view=winrt-19041>
- Microsoft `PackageManager.AddPackageByUriAsync`, reviewed but not selected: <https://learn.microsoft.com/en-us/uwp/api/windows.management.deployment.packagemanager.addpackagebyuriasync?view=winrt-26100>
- Microsoft `AddPackageOptions`, reviewed but not selected: <https://learn.microsoft.com/en-us/uwp/api/windows.management.deployment.addpackageoptions?view=winrt-26100>
