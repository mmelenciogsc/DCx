# ADR 0061: Derive an exact local-dependency transfer and capacity preflight

- Status: Accepted for read-only transfer review; acquisition remains blocked
- Date: 2026-10-18

## Context

The authenticated P06 catalog pins a Whisper CUDA runtime, two Whisper model candidates and one Ollama embedding manifest with managed layers and license evidence. A prose byte total can drift when any manifest, layer or license length changes. It can also double-count a shared license or omit a managed blob. Asking for transfer approval against such a total would not bind the complete operation or prove that temporary and retained copies fit on the destination volume.

## Decision

Derive a schema-v1 transfer plan only from an authenticated `LocalModelDependencyCatalogReview` and an explicit, unique candidate set. The planner validates every candidate again, expands Ollama manifests into exact registry blob URLs, adds license evidence, and deduplicates only when an exact source URL, byte length and SHA-256 agree. A conflicting identity for one URL fails closed. Items retain sorted purpose roles so deduplication does not hide why a file is required.

The domain-separated plan SHA-256 binds the catalog identity and digest, sorted candidate IDs, every sorted source URL, byte length, digest and role, total transfer bytes, and the storage formula. The current full catalog produces nine immutable transfer items, 1,281,058,770 transfer bytes and plan SHA-256 `a0d997c6298fdf5f592241928c6fd9564c6de0c3e023c9eb69bd1df96406c286`.

Minimum free storage is two exact transfer copies plus a 512 MiB workspace reserve: 3,098,988,452 bytes for the current plan. One copy represents incoming temporary bytes and one represents retained verified bytes. Observed free capacity is displayed but excluded from the authority digest because it is volatile. It must meet the minimum and be rechecked immediately before any future transfer.

The keyboard/UIA-readable local-model window exposes the exact plan, every source identity, totals, capacity result and a direct statement that no approval exists. The preflight performs no network request, download, extraction, pull, install, registration, selection, execution or persistence.

## Consequences

- Catalog edits cannot silently leave a stale transfer or storage estimate; exact regression values and plan digest must change together.
- Shared Whisper license evidence and the Ollama license layer are transferred once without losing their candidate/layer roles.
- Capacity failure or an unknown observation cannot be represented as sufficient.
- The plan is review evidence only. It does not authenticate a publisher, accept license terms, create consent, acquire bytes or authorize later use.
- A future downloader must add its own explicit, accessible, item-by-item approval, exact-origin transport, streaming size/digest checks, redirect policy, cancellation, protected receipt, capacity recheck and cleanup. None of those authorities are inferred from this preflight.
