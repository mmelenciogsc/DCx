# ADR 0093: Enroll one exact global registry root and origin through explicit protected review

- Status: Accepted for per-user product composition; production registry/key deployment remains blocked
- Date: 2026-10-19

## Context

ADR 0092 deliberately left normal startup unconfigured because an environment variable, mutable file, discovered endpoint or bundled simulation key would silently create global trust. DCx needs a usable bootstrap that does not make opening Global knowledge a network action, confuse enrollment with synchronization, or grant authority from an unreviewed path or URL.

## Decision

Global knowledge exposes a separate **Registry enrollment** action. The user selects one bounded local, regular, non-reparse file containing one exact P-256 SubjectPublicKeyInfo public key and enters one exact registry base address. Remote addresses require HTTPS; HTTP is accepted only for a literal loopback address used in validation. User information, queries, fragments, oversized addresses, non-P-256 keys and partially consumed key encodings fail closed.

Review reads the exact key bytes, canonicalizes the base address and displays only the key file name, byte count, SHA-256 fingerprint, canonical address and review time. It opens no network connection. Approval begins disabled and unchecked. Saving requires a separately checked approval bound to that review within 30 minutes. Editing or choosing **Change candidate** destroys the held key copy and clears approval.

The approved schema, canonical address, public key, public-key digest and approval time are stored solely in the current user's Windows Credential Manager under a versioned target. Strict JSON rejects unknown members, malformed Base64, digest conflicts, noncanonical addresses and unsupported versions. Re-enrollment replaces the exact authority only after another review and approval. Clearing requires a separate initially unchecked acknowledgment; it removes enrollment authority but does not delete cached signed generations.

Normal product composition reads the protected enrollment. If absent, search and synchronization remain unavailable without creating replica storage or an HTTP handler. If valid, it opens only the digest-namespaced signed replica. Status and search remain offline. Only **Synchronize** uses a bounded 30-second client for the exact enrolled origin, with redirects, cookies, proxies and automatic decompression disabled. Changing or clearing enrollment disables the already-open source and requires closing and reopening Global knowledge, preventing continued use of stale authority. Malformed protected state opens a fail-closed recovery surface that permits replacement or clearing without inferring registry state.

The enrollment window is keyboard/UIA accessible, has unique access keys, assertive stable statuses and deterministic focus return. Its validation fixture uses an isolated in-process store. It neither reads nor writes the user's real credential.

## Validation

Focused P09 coverage passes 25/25. It includes exact P-256 review, remote-HTTP/P-384/reparse denial, malformed/digest/canonicalization denial, a unique-target real Windows Credential Manager save/replace/clear round trip, and source composition proving zero storage/HTTP-handler creation while unconfigured. With a valid enrollment, open/status/search make zero requests; the first request occurs only on explicit synchronization and targets the exact canonical origin. Disposing the source disposes its network lifetime.

Native UIA proves heading focus, seven unique enrollment access keys, disabled/unchecked initial approval, exact fingerprint/address review, path minimization, candidate-change invalidation, save/remove interlocks, zero enrollment-fixture sockets, production boundary text and focus return. The aggregate evidence is `artifacts/test-results/p09-f1f931e9e77f4895a560a29904df8954/` from implementation `65d9718`. The full Windows suite passes 595/595.

## Consequences and residual risk

- A user can configure normal DCx without a bundled key or environment-selected trust authority.
- Enrollment is not synchronization, server deployment, key provenance, TLS acceptance evidence or content endorsement. Users must verify the displayed fingerprint through an independent trusted channel.
- Credential Manager protects the record from casual filesystem replacement but is not hardware-backed monotonic storage and cannot resist arbitrary malicious code already running as the same user.
- Cached generations survive revocation but become unavailable until the same exact root is enrolled again. Recovery and secure removal policy remain production work.
- No production root, origin, certificate, key ceremony or server was created or approved by this milestone.
- Docker/Compose execution, malicious same-user isolation, human JAWS/NVDA/Narrator acceptance and the remaining P09 gates stay blocked.

## Alternatives rejected

- Bundle a production root/origin before key custody and deployment approval: creates authority without an approved ceremony.
- Read root/origin from environment variables or an ordinary settings file: permits silent same-user substitution and makes test configuration unsafe for production.
- Contact the address during review/save/open: conflates trust admission with network synchronization and breaks offline-first behavior.
- Preserve approval after any candidate edit: allows a changed root or origin to inherit authority reviewed for different bytes.
- Delete cached data when clearing enrollment: expands a trust-revocation action into destructive local-data removal without separate approval.
