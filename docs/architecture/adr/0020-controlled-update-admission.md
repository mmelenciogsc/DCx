# ADR 0020: controlled update admission

- Status: Accepted for pre-install admission only
- Date: 2026-10-17

## Context

P13 requires controlled updates, but DCx has no approved MSIX toolchain, signing identity, production update origin or clean-VM installer evidence. Downloading a package based only on TLS or a mutable JSON response would create downgrade, redirect, substitution and identity-confusion risks. Pre-install work must not imply permission to install or activate software.

## Decision

Admit update metadata only as a strict canonical envelope signed by a pinned ECDSA P-256 key. Bind DCx product identity, canonical four-part MSIX version, issue/expiry window of at most 30 days, HTTPS package URI, exact length/SHA-256, package identity and publisher. Require the candidate to be newer than the installed version and no more than five minutes in the future.

Use an exact-origin HTTPS transport with redirects, cookies, proxies and automatic decompression disabled. Bound manifests to 64 KiB and packages to 1 GiB, require reviewed media types and stream to a non-reparse staging root. Before retaining bytes, hold a no-write read lease while checking length/digest and an injected platform package-signature/identity/publisher/version result. Reverify retained packages on reuse and store the exact signed manifest atomically.

Never execute, install, elevate, restart or activate from this component. A later installer transaction must independently reverify the final package immediately before activation.

## Consequences

- Network/TLS compromise alone cannot substitute a package without the manifest key and matching package-trust evidence.
- Equal/downgrade, stale/future, cross-origin, malformed, oversized, digest-mismatched, identity-mismatched and post-stage-tampered inputs fail before an install action exists.
- Current evidence uses an ephemeral key, synthetic bytes, a fixture trust verifier and non-network handler. Production key custody/rotation, a real Windows MSIX/Authenticode verifier, accessible user approval, installer/update/rollback transactions and clean-VM evidence remain blockers.
