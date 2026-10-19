# ADR 0111: Revalidate the final staged update without activation authority

- Status: Accepted for a read-only local readiness review; installation and activation remain absent and blocked
- Date: 2026-10-19

## Context

ADR 0110 made publication of the inert package and signed manifest atomic, but the last verified stage result was not a distinct pre-activation review. A future installer must not treat successful download, staging or a stale user approval as enduring execution authority. It needs a narrowly reusable verification primitive that reopens the final staged files, checks current manifest validity and package trust, and produces review evidence without installing anything.

## Decision

- Add a separately requested, default-off final staged-set integrity review after successful inert staging.
- Before any filesystem access, require the exact earlier review, unchanged installed-version input and a separate explicit request. An unchecked request fails without creating storage or invoking trust verification.
- Reopen the exact version-and-package-digest directory. Require exactly `package.msix` and `manifest.json`, reject reparse/missing/extra members, decode and verify the locally retained canonical signed envelope, reapply issue/expiry/origin/version policy, require exact equality with the earlier review, and recheck package length, SHA-256, signature, identity, publisher and version.
- Bind the exact review digest, revalidation UTC time, candidate version, package/envelope digests, length and `ExecutionPermitted=false` into a domain-separated readiness SHA-256.
- Expose no package path, executable, command, argument, token, secret or credential through the user-facing source contract. Network, installer, elevation, activation, restart and production-action counters must remain zero.
- Reset the one-use request after either success or failure. Normal composition remains `not_configured`; the validation fixture remains environment/root gated and synthetic.
- Treat the receipt only as evidence of one completed read-only review. It is not transferable or durable install authority. Any future approved installer must perform its own final verification while holding the files against replacement and immediately before its transactional activation step.

## Validation

Implementation `b4fa789` plus workflow-order correction `c5a4ecd` passes 33/33 P13 reliability tests and the full 673/673 Windows suite. Exact P13 evidence is `artifacts/test-results/p13-611d9d30107847d992e6b6f543be2f58/` at `c5a4ecd`.

Core cases cover unchecked denial before trust work, successful local-only revalidation, bound receipt fields, current-version substitution, expiry, cancellation, missing set, package tamper, wrong-key manifest substitution, extra members and every nonzero authority field. Native UIA proves six distinct access keys, default-unchecked request, denial focus, one readiness digest, request reset, unchanged two-file staged set and zero network/installer/elevation/activation/restart/production actions.

## Consequences and residual risk

- Final staged bytes can be rechecked through the same strict manifest/package policy without adding execution capability.
- The receipt makes the time and exact reviewed inputs auditable while explicitly denying execution.
- A malicious same-user process can still modify user-writable staged files after this review. The receipt therefore cannot be consumed later as activation permission.
- Production origin/signing-root custody, certificate/revocation policy, rollback protection, installer transaction/rollback, signed clean-VM lifecycle, independent assessment and human JAWS/NVDA/Narrator acceptance remain blocked.
