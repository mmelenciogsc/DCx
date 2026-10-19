# ADR 0110: Atomically publish complete controlled-update sets

- Status: Accepted for local inert staging; activation remains absent and blocked
- Date: 2026-10-19

## Context

The controlled-update boundary verified an approved package before retaining it, then wrote the exact signed manifest beside it. A process termination between those two operations could leave a final-looking package without its manifest. No activation path consumed that package, but a crash-visible partial set was needlessly ambiguous and a future consumer could mistake individual-file presence for complete publication.

## Decision

- Stage the package and exact signed envelope inside a unique `.incoming-<guid>` directory under the final `packages` parent.
- Verify package length, SHA-256 and trust evidence, then atomically write and digest-check the signed envelope before publication.
- Publish only by renaming the complete incoming directory to the version-and-package-digest final directory on the same volume.
- Admit or reuse only an exact two-file final set named `package.msix` and `manifest.json`. Reject missing, extra or reparse entries, changed manifest bytes and package integrity/trust changes.
- Revalidate the complete set after publication. If concurrent stagers race, the loser verifies and reuses the winner's exact final set; it never overwrites it.
- Orderly cleanup is confined to the unique incoming directory and its two known files and fails closed on unexpected content. An incomplete incoming directory is never a published set and is never returned as admission.
- Keep package activation, installation, elevation and restart outside this contract.

## Validation

Implementation `2f0c64d` adds partial-transfer, missing/extra/changed-manifest and concurrent-stage cases. P13 gate-readiness correction `13183fd` waits for enabled UIA controls between asynchronous diagnostics actions.

Exact P13 evidence at source `13183fd` is `artifacts/test-results/p13-5e3fa594c3c34a1eaf909ee49e894c63/`: 30/30 reliability, 21/21 package-trust and 3/3 WebView2 policy tests. Native controlled-update UIA retained one directory containing exactly two files, no loose package files and no residual incoming directory. Network, installer, elevation, activation, restart and production-action counters were zero. The full Windows suite passed 670/670.

## Consequences and residual risk

- A cooperating consumer cannot observe a final directory containing only one member of the approved set.
- Concurrent cooperating stagers converge on one verified immutable name rather than overwriting one another.
- Abrupt termination can leave a nonpublished `.incoming-*` directory. This is bounded ambiguity rather than activation authority; a future garbage collector must use a separately reviewed age/ownership policy and must never recursively delete an unresolved path.
- A malicious process running as the same user can still modify user-writable staged data outside the verification lease. Any future activation path must re-open and reverify the exact final set immediately before use and requires separate approval, protected production trust roots and signed clean-VM lifecycle evidence.
