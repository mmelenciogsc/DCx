# ADR 0064: Publish only protected complete local-dependency transfer sets

- Status: Accepted for inert local coordination; product acquisition remains blocked
- Date: 2026-10-18

## Context

ADR 0063 validates one approved response stream but intentionally chooses no destination and retains no receipt. P06 needs a set boundary that cannot expose a partially downloaded candidate collection as complete, does not disclose local source paths, rechecks volatile capacity immediately before work and detects filesystem-only replacement after restart. Startup cleanup must not sweep work while another transfer is active.

## Decision

Add a private per-user transfer-set coordinator around the item transport. One explicit set approval binds the authenticated plan digest. Before creating transfer storage, the coordinator revalidates the plan, re-observes free space on the application-data volume and requires the conservative minimum. It creates one random `.incoming-<128-bit hex>` directory under confined non-reparse local-model storage and one deterministic digest-derived payload name per item. Source URLs and local paths are absent from file names and the receipt.

Every exact item must complete through the ADR 0063 transport and return matching plan/source/length/digest evidence. The coordinator then writes a strict bounded deterministic receipt containing only catalog/plan identity and indexed file length/digest metadata. It protects the exact receipt length and SHA-256 through the existing current-user Windows Credential Manager approval-anchor boundary before atomically renaming the incoming directory to the plan digest. Existing completed destinations are never overwritten.

Read-only verification requires the protected anchor, exact receipt schema/inventory and the complete directory inventory, then rehashes every payload under a no-write/no-delete read share. Missing protection, changed/extra/missing/linked files and digest/length differences fail closed. Failure, protected-write denial and cancellation remove only the operation's random incoming directory without following links. A separate cleanup method recognizes only strict incoming names and refuses linked content; it is not called automatically by transfer. It may be wired only under the product's exclusive data-root lease when no transfer is active.

## Consequences

- A complete directory becomes observable only after every item and the protected receipt pass.
- Filesystem-only receipt/payload replacement cannot verify without the current-user protected fact. This is not isolation from arbitrary same-user code.
- Crash leftovers can be cleaned narrowly at a future controlled startup, without broad recursive deletion or automatic peer sweeping.
- The coordinator remains disconnected from the desktop and all tests use synthetic in-memory transfer data. It creates no authority to contact a provider.
- Provider redirect/direct-origin approval, real contained-license review, human-accessible progress/cancel/recovery UX and the explicitly authorized real benchmark remain blocked.
