# ADR 0016: default-off contribution simulation API

- Status: Accepted for local simulation
- Date: 2026-10-17

## Context

P10 had authoritative contribution policy, quarantine, reviewer transitions, signed publication and withdrawal, but those paths were reachable only from in-process fixtures. A process-boundary test is required without implying that local fixtures are a production identity, ingress or deployment design. Raw credentials must not be committed, logged or accepted as identity claims from request bodies.

## Decision

Keep every contribution route absent unless `DCX_GLOBAL_CONTRIBUTION_API=1` and all required settings pass startup validation. Configure distinct contributor and reviewer bearer credentials only by their SHA-256 digests, and bind each role to one bounded server-configured pseudonymous identity. Derive role and identity from authentication, compare hashes in constant time and erase transient token buffers.

Use a versioned binary envelope containing strict reviewed metadata and an optional raw artifact. Share exact content and size bounds across the codec, Kestrel and endpoint reader. Expose contributor intake, owner-scoped status and withdrawal; expose reviewer pending/status and approval. Keep the Windows contribution surface no-upload.

Admit CCB artifacts into quarantine but fail approval closed unless the server has an explicitly enabled Linux executor with hash-pinned Bash, launcher, Bubblewrap and static runtime. Keep contribution bodies and bearer credentials out of process diagnostics.

## Consequences

- Real child-Kestrel tests cover missing/invalid/wrong-role credentials, identity mismatch, idempotent intake, approval replay, malicious quarantine, signed synchronization and owner withdrawal.
- The default server remains read-only for contribution purposes and returns `404` for contribution routes.
- Static hash-configured bearer credentials provide deterministic simulation evidence only. ADR 0019 adds bounded process-local pressure controls, but neither decision provides production TLS, user lifecycle, OAuth/OIDC or mTLS identity, distributed proxy-aware rate limiting, ingress policy, credential rotation/revocation, operator audit or account recovery.
- No user content is uploaded by the Windows UI and no production service is enabled by this decision.
