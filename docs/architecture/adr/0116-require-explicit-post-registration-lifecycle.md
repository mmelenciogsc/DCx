# ADR 0116: require explicit post-registration application lifecycle

- Status: Accepted for the lifecycle contract foundation; close/relaunch execution remains unavailable and blocked
- Date: 2026-10-20

## Context

The internal uncomposed Windows adapter can report a successful package registration, and the activation result already distinguished an operating-system restart from a validation no-op. It did not separately state that the currently running DCx process must close and a new DCx process must launch before the user can rely on the registered version. Treating `RestartRequired=false` as “no lifecycle work” would permit a future caller to omit that necessary boundary.

DCx still lacks an approved signed package, activation route, close/relaunch coordinator and clean VM. The contract can nevertheless fail closed now, without creating process or deployment authority.

## Decision

1. Add separate `ApplicationCloseRequired` and `ApplicationRelaunchRequired` fields to the closed activation-execution result.
2. Admit only three exact shapes:
   - `ValidationNoOp`: zero PackageManager calls, not registered, no application close, no relaunch and no operating-system restart.
   - `Registered`: one PackageManager call, registered, application close and relaunch both required, no operating-system restart.
   - `RegistrationDeferred`: one PackageManager call, not yet registered, application close and relaunch required, and operating-system restart required.
3. Make the internal Windows executor return the `Registered` lifecycle shape. Reject a success that omits either application requirement.
4. Extend the validation-only public result with the two requirements plus application-close-request and application-relaunch-attempt counters. Its policy accepts only false requirements and zero counters.
5. Announce these lifecycle values in the accessible native validation result and require their exact zero-authority values in both the native and parent P13 gates.

## Consequences

- A future real registration cannot be represented as complete application lifecycle coordination. Its result explicitly requires DCx close and relaunch.
- A deferred registration cannot hide its operating-system restart requirement.
- The current validation no-op proves that it requested no close, attempted no relaunch and required neither action.
- No lifecycle coordinator, helper process, relaunch identity, shutdown request, restart, PackageManager call or activation route was added. Those remain separately approval- and clean-VM-gated.

## Validation

Implementation `7569673` passes 24/24 focused activation/staging/source tests and the full 683/683 Windows suite with zero build warnings/errors. Exact P13 evidence is `artifacts/test-results/p13-4a21a909355743e5ac1a7952c9987897/`: 42/42 reliability, 22/22 package trust and 3/3 WebView2 policy tests plus native UIA. Application-close required, application-relaunch required and operating-system-restart required were false; close requests, relaunch attempts and PackageManager calls were zero. All other authority counters were zero. Exact P14 evidence `artifacts/test-results/p14-9f497a55a8ef481ba16b29c617bb4065/` remains `NO_GO` with seven blockers and activation false.
