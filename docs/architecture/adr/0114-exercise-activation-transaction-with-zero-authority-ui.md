# ADR 0114: exercise the activation transaction through a zero-authority accessible workflow

- Status: Accepted for synthetic validation; package activation remains unavailable and blocked
- Date: 2026-10-19

## Context

ADRs 0112 and 0113 established a lease-held activation transaction and a concrete but uncomposed Windows package adapter. Unit tests proved the transaction and adapter separately, but the native controlled-update workflow stopped after the final read-only readiness receipt. DCx still needed evidence that keyboard and screen-reader users can review and grant a distinct one-use transaction-validation approval without making the real adapter reachable or representing synthetic execution as package activation.

## Decision

Extend the provider-neutral update source with a closed activation-validation result. It accepts only the exact review/readiness/version tuple, `ValidationNoOp`, zero PackageManager invocations, no registration or restart requirement, and zero network, child-installer, elevation, activation, restart and production actions. Every other status, counter or binding fails closed. No path, command, argument, credential, token or secret is added to the user-facing contract.

The environment/root-gated synthetic source may consume one explicit approval and call the real lease-held transaction with a validation-only executor. The executor returns only `ValidationNoOp`; it cannot reference or instantiate the Windows adapter. The source rejects replay. Normal composition remains `not_configured` and throws before any transaction can be reached.

After the separate final staged-set review, the native window enables a new approval checkbox that defaults unchecked and a distinctly labeled no-install validation action. Denial restores focus to the checkbox. Success reports the exact validation status, zero PackageManager calls, no registration/restart and all authority counters. The controls reset after one attempt. The Windows adapter remains unregistered and uninvoked.

## Consequences

This composes the transaction foundation through the accessible validation fixture and independently proves that the staged package and manifest hashes are unchanged across it. It does not create installation authority, validate the real adapter against Windows, provide application-close/restart coordination, define rollback/uninstall, or satisfy signed-positive, clean-VM, independent-security or human JAWS/NVDA/Narrator acceptance. Those remain P13 blockers.

## Validation

Implementation `63100d1` passes 42/42 P13 reliability, 21/21 package-trust and 3/3 WebView2 policy tests, the full 682/682 Windows suite and 17/17 P14 tests with zero build warnings/errors. Exact P13 evidence is `artifacts/test-results/p13-c18e4b6bf1c5409fbb65dbc0f5422026/`. Native UIA proves eight distinct access keys, default-unchecked denial/focus, one no-op transaction, approval reset, unchanged two-file staged-set hashes, zero PackageManager calls and every authority counter at zero. Exact P14 evidence is `artifacts/test-results/p14-0055b24a5ac442e19a55f95859ed9a3f/`; it remains `NO_GO` with seven blockers and production activation false.
