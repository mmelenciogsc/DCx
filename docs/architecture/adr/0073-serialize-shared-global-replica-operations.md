# ADR 0073: Serialize operations on one shared global replica

- Status: Accepted for the per-user replica; production deployment and malicious same-user isolation remain blocked
- Date: 2026-10-18

## Context

`GlobalReplicaClient` originally serialized operations only within one object instance. Two instances opened on the same trust-root replica could therefore write the same transfer checkpoint, trust-policy file, artifact destination or active pointer concurrently. The normal desktop process has a broader application-data lease, but the reusable client library, recovery paths and another cooperating process could not rely on that UI lifetime boundary. A collision normally failed on an exclusive checkpoint handle, while conflicting trust-policy activity could leave the signed filesystem policy out of step with its protected Credential Manager anchor.

## Decision

Every public replica operation and replica-open integrity check obtains one replica-scoped operation lease before loading or mutating policy, release, catalog, checkpoint, artifact or active-pointer state. A process-wide semaphore keyed by canonical replica root serializes client instances in one process. A persistent regular lock file opened with no sharing extends the lease across cooperating processes. The lock file is never deleted during normal release, avoiding the close/delete/recreate race; it contains no identifier or operational content.

Acquisition revalidates the replica root and lock file through the P09 storage guard before and after open. Contention waits in short cancellable intervals for at most 30 seconds, then returns a stable path-free retry message. Cancellation releases the local gate, and a released external handle permits a later operation to recover. A linked lock file fails closed without modifying its target.

Read paths use the same exclusive boundary because loading a trust policy may reconcile the protected revision anchor. Signed generations and atomic pointer replacement remain the data-integrity boundary; the lease prevents cooperating clients from racing those steps but does not replace any signature, digest, chain or path verification.

## Consequences

- Two clients opened on the same replica serialize a blocked synchronization. The first activates one release; the second then observes it as already current, with no orphan checkpoint.
- Status, search, rollback, policy, synchronization and lazy-artifact operations are cancellable while another client owns the lease.
- A process that crashes releases its operating-system handle; the persistent empty lock file is reusable.
- A malicious process running as the same Windows user can still tamper between separate path checks and opens, delete Credential Manager state, or deny service. Production still requires a dedicated least-privilege identity, ACL/mount policy, hardware-grade monotonic trust where approved, deployment isolation and external security review.
- This decision does not run Docker Compose, deploy the Ubuntu service, or grant installation/elevation authority.

## Alternatives rejected

- Rely on the desktop-wide application-data lease: library and cooperating-process callers are not guaranteed to share that lifetime.
- Keep only the per-object semaphore: it does not coordinate two client objects or processes using the same replica.
- Delete the lock file on release: close/delete/recreate ordering can split lock ownership, particularly on filesystems that permit unlinking an open file.
- Use reader sharing for status/search: trust-policy load can advance the protected crash-recovery anchor, so those paths are not purely read-only.
