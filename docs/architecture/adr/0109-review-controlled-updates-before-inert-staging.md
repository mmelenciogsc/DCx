# ADR 0109: Review controlled updates before inert staging

- Status: Accepted for local inert staging; production update service and activation remain blocked
- Date: 2026-10-19

## Context

The controlled-update core verified a signed manifest and package before retaining inert bytes, but it had no native user review surface. A single check-and-stage action would make manifest review, package retrieval and storage difficult to distinguish for keyboard and screen-reader users. It would also permit a changed manifest to be substituted between review and staging unless the second action were bound to the exact first result.

## Decision

- Split the core into `ReviewAsync` and `StageReviewedAsync`. Review verifies the canonical ECDSA P-256 envelope, current/candidate versions, validity, exact HTTPS origin and package metadata without requesting package bytes or creating staging storage.
- Bind the displayed review with a domain-separated SHA-256 over the manifest address, current/candidate versions, issue/expiry times, package address/length/digest/identity/publisher and signed-envelope digest.
- Require separate explicit approval for inert staging. Before any package request, re-download and reverify the signed manifest against the configured trust root and require the resulting review to equal the approved review exactly. Current-version substitution, review mutation, expiry or manifest change fails closed.
- Only after that fresh exact match, stream and verify the package under the existing length, digest, signature, identity, publisher and version rules. Retain only the package and exact signed envelope. Do not expose an installer, executable path, shell command, elevation, activation or restart method through the user-facing source contract.
- Keep normal desktop composition honest and unavailable until a production origin and protected signing root are separately provisioned and approved. The normal window infers no update state and makes zero requests.
- Provide a separately environment/root-gated validation source that generates an ephemeral signing key and synthetic in-memory manifest/package. It exercises the real review and staging core but makes zero network requests and reports execution, installer, elevation, activation, restart and production-action counters as zero.
- Expose the workflow through a native WPF window with heading focus, exact read-only details, four distinct access keys, assertive status, default-unchecked approval, denial focus and one-use approval reset. A fresh check is required before another stage attempt.

## Validation

Implementation `c506bb7` passes 27/27 P13 reliability tests and the full 667/667 Windows suite. Exact P13 evidence is `artifacts/test-results/p13-a43cd4b336f14798b43d2c1cbe768af8/`.

Native UIA proves that an ungated fixture launch is denied, review requests one manifest and zero packages, unchecked staging creates no storage, approved staging rechecks the manifest and retains exactly one package plus one signed envelope, and approval cannot be replayed. Access keys are `C`, `A`, `S` and `L`. Fixture network requests, child installer processes, elevation requests, activation attempts, restart attempts and production actions are all zero. The normal product source reports `not_configured`, makes zero requests and infers no update.

## Consequences and residual risk

- Users can distinguish checking, exact review and inert staging without granting installation authority.
- Fresh manifest equality closes the review-to-stage substitution window for cooperating product code. It does not replace protected production signing-root custody, revocation, rollback protection or a final-file recheck immediately before a future activation.
- The synthetic trust verifier proves composition only; it is not signed-positive Windows package evidence. Real signed update/install/rollback/uninstall behavior, production feed provisioning, clean-VM validation, independent security assessment and human JAWS/NVDA/Narrator acceptance remain blocked.
