# ADR 0009: Offline-root-signed global trust policy

- Status: Accepted
- Date: 2026-10-17

## Context

Pinning the authoritative catalog directly to one online release-signing key makes compromise recovery require a client update. Accepting only the newest policy prevents offline clients from verifying revisions they missed, while accepting unsigned revision jumps permits rollback or freeze attacks.

## Decision

DCx pins an offline ECDSA P-256 trust root by subject-public-key info. That root signs canonical, bounded policy snapshots. Each policy identifies release keys by the SHA-256 of their public-key encoding and assigns a first sequence, optional last sequence and optional revocation sequence.

Policies have monotonically increasing revisions and bind the previous signed-policy envelope digest. The authoritative registry retains every policy revision. Clients fetch and verify each missing revision, in order and before release synchronization. The release itself remains bound to its prior release digest and resulting catalog digest.

Rejected rollback targets are verified before the active pointer changes. Legacy deployments without a policy continue to treat the pinned root as the release key, but a configured policy becomes the exclusive release-key authority.

## Consequences

- An online release key can rotate or be revoked without replacing the offline client trust anchor.
- Offline clients can catch up deterministically without skipping policy-chain evidence.
- Policy history and bounded range transport become part of the authoritative registry contract.
- Production still requires a documented offline-root custody and ceremony, backup/recovery, TLS identity and an OS- or hardware-protected minimum policy revision. Local files alone do not provide hardware-backed anti-rollback.
