# ADR 0055: Pin and review local-model dependencies without installation authority

- Status: Accepted for consent-safe offline admission
- Date: 2026-10-18

## Context

P06 cannot run real transcription or embedding acceptance until suitable dependencies exist, but discovering a candidate must not silently authorize downloading, installing, extracting, executing or pulling a model. Mutable release URLs, tags and manifests are insufficient identities. A staged file can also be substituted through a linked path or altered between a superficial check and later use.

## Decision

Embed a bounded schema-v1 catalog that fixes each reviewed candidate to an HTTPS source, exact byte count, lowercase SHA-256 digest, license source and license digest. The Ollama candidate additionally fixes the exact model, license and parameter layer media types, sizes and digests. The embedded catalog itself has a compiled SHA-256 identity and an exact candidate inventory.

Catalog parsing rejects unknown or duplicate JSON members, unsupported schema versions, unapproved hosts, non-HTTPS or decorated URLs, duplicate identities, inconsistent file types, incomplete layer inventories and inconsistent license layers. External catalogs are accepted only from a fully qualified local non-reparse file and only when the caller supplies its expected digest.

Offline artifact review opens an existing local regular file with a no-write/no-delete read lease, revalidates the path after the lease is acquired, verifies its extension, exact size, container/model signature, semantic Ollama descriptors and exact SHA-256, and returns a content-minimized review record. It does not copy, extract, execute, install, pull or make a network request. Runtime archives remain subject to contained third-party notice review after staging.

The native local-model window authenticates and exposes the embedded catalog through keyboard-focusable UIA controls. It shows exact source, size, digest, license and managed-layer evidence. A user may browse to an already-staged local artifact and invoke only the offline reviewer. The surface provides no download, install, pull, extraction, execution, approval or selection action, and a successful integrity result is explicitly not legal or production approval.

The catalog is an admissibility input, not consent. Any download or model pull still requires explicit owner authorization. Any production selection still requires the existing accessible, default-off license review and approval plus real quality/resource/cancellation/unload gates.

## Consequences

- The proposed P06 payload can be referred to by immutable identities without changing model state.
- A linked catalog/artifact, linked ancestor, wrong length, wrong digest, invalid signature or Ollama layer substitution fails closed.
- The reviewer cannot confer provenance merely from a matching digest; source retrieval records, archive-contained notices and legal approval remain required.
- Same-user kernel-level path races are narrowed by the read lease and post-open revalidation but still require production ACL/process isolation.
- P06 remains blocked until the owner authorizes staging and the real acceptance gates pass.
