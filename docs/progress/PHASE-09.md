# P09 — Global registry and synchronization

- Status: BLOCKED (native process/HTTP simulation passes; required Docker Compose execution unavailable)
- Partial implementation commits: `1be3f9e`, `b1608cd`, `9123a81`, `4278601`, `7df8c0a`, `0a2dab8`, `6239834`, `65d9718`; read-only environment audit `8b94159`
- Documentation commit: recorded by the commit containing this file

A bounded read-only SSH capability audit was added later; no deployment, remote write, container start, service change or privileged action was attempted.

## Implemented

- Linux-capable ASP.NET Core service with an authoritative SQLite release/artifact database and local-simulation-only startup gate.
- Canonical ECDSA P-256 signed release manifests binding sequence, previous release checksum, changes and resulting catalog checksum.
- Upsert, deprecation and removal reducer with duplicate/version/conflict denial.
- Content-addressed authoritative artifacts; a release cannot reference absent or conflicting artifact metadata.
- Strict HTTPS remote transport policy with loopback HTTP allowed only for local simulation; bounded descriptor and byte-range endpoints.
- Per-trust-root global client namespace physically separate from every P08 private profile namespace.
- Checkpointed bounded release/artifact chunks, checksum and signature verification, monotonic chain checks, canonical result verification and atomic active-pointer replacement.
- Locally searchable offline catalog with optional vectors, explicit rollback to retained verified generations and lazy artifact download.
- Canonical offline-root-signed trust policies with SHA-256 public-key identities, per-sequence validity, effective-sequence revocation and a bounded 64-key policy.
- Durable authoritative trust-policy history and bounded HTTP descriptor/chunk endpoints; clients catch up every missed revision and verify the complete hash-linked policy chain before releases.
- Policy replay/conflict and unauthorized old-key releases fail closed. Rollback candidates are fully verified under the current policy before the active pointer changes.
- Read-only hardened Compose definition, non-root container, loopback host port, no-new-privileges, excluded secrets/build artifacts and externally supplied signing key.
- Atomic publication-operation records bind a stable operation ID to the canonical ordered change-set digest and signed release sequence. An identical retry after repository reopen returns the original descriptor; reuse with changed content fails closed.
- The Windows client defaults to a strict per-user Credential Manager anchor containing the trust-root digest, highest accepted trust-policy revision and exact policy digest. A lower, missing or same-revision-conflicting local policy fails closed.
- Trust-policy updates commit the signed file before advancing the protected anchor. The next open recovers only the exact one-revision crash window whose signed predecessor digest equals the anchor; arbitrary jumps are denied.
- The protected floor applies to policy revisions rather than release sequences, preserving explicit rollback to a retained release only when that release remains valid under the current anchored policy.
- Shared storage admission now rejects filesystem roots, network paths, reparse ancestors, swapped replica/server subdirectories and linked pointer/database/checkpoint/artifact files. Client operations recheck their complete layout. Authoritative artifact metadata must resolve to the exact digest-derived path, SQLite/WAL/shared-memory paths are admitted before use, and the externally supplied signing key must be one exact regular P-256 PKCS#8 file.
- Every replica-open check and public status, policy, sync, rollback, search and lazy-artifact operation now takes one replica-root-scoped lease. A canonical-root process gate serializes client instances, while a persistent regular no-share lock file serializes cooperating processes without a delete/recreate release race. Contention is cancellable and bounded; unsafe linked lock files fail closed without modifying their target.
- The native shell now exposes a modeless, keyboard/UIA-accessible Global knowledge window. It distinguishes unconfigured state from an empty verified generation, shows active release/trust-policy metadata, searches only the separate local replica and never synchronizes automatically.
- Explicit access-keyed synchronization uses the existing signature/chain/atomic-activation path. Search is capped at 512 characters and 20 displayed records, rejects non-whitespace controls before storage work and exposes human-readable CKB/CCB item names plus read-only catalog metadata.
- Normal startup now offers a separate accessible enrollment window for one exact bounded local P-256 SubjectPublicKeyInfo trust root and one canonical HTTPS registry origin. Literal-loopback HTTP remains validation-only. Review displays the base file name, byte count, exact SHA-256, canonical address and UTC time without contacting the registry.
- Approval begins disabled and unchecked, expires after 30 minutes, and is invalidated by any candidate change. The strict versioned record is stored only in current-user Windows Credential Manager. Replacement repeats review/approval; removal has a separate initially unchecked acknowledgment and does not delete cached generations.
- Normal source composition creates no storage or HTTP handler while unenrolled. An enrolled source opens the digest-namespaced replica offline; only explicit synchronization can use the exact origin through a no-redirect/no-cookie/no-proxy/no-decompression 30-second client. Enrollment changes disable the old open source until the user closes and reopens Global knowledge.
- Malformed protected enrollment fails closed into the same review/clear workflow. No root, endpoint or authoritative state is inferred. Source and network lifetimes are disposed with the window, and concurrent open attempts are suppressed.
- Separate dual-gated fixtures compose the real signed-replica window and the exact enrollment UI. The enrollment fixture uses an isolated in-process store, never touches the user's real credential and opens no socket.

## Reproduction

```bash
cd /mnt/d/DCx
pwsh.exe -NoProfile -ExecutionPolicy Bypass -File D:\\DCx\\scripts\\validate-p09.ps1
pwsh.exe -NoProfile -ExecutionPolicy Bypass -File D:\\DCx\\scripts\\ci.ps1
```

## Measured result

- Locked restore and native Release build passed with 0 warnings/errors.
- P09 exact filtered tests: passed 25, failed 0, skipped 0; about 1 second, with exact counters parsed from retained TRX.
- Current full suite: passed 595, failed 0, skipped 0; about 19 seconds.
- A real child Kestrel process served three chained trust-policy revisions and a signed SQLite-backed release over loopback HTTP; a fresh production client transport caught up all revisions, synchronized and searched it.
- Two distinct client roots synchronized the same chain; the P08 private sentinel remained present and absent from global results.
- Wrong-key release activation was rejected with no active pointer.
- A root-signed rotation revoked the old release key from sequence 2. An old-key sequence-2 release, an attacker-signed policy and a policy replay were rejected; the authorized replacement key succeeded.
- A rollback to a generation invalidated by an emergency revocation was rejected before pointer replacement, leaving the active sequence usable across reopen.
- A real Windows Credential Manager round trip proved default client wiring and trust-root isolation. Restoring a lower root-signed policy file, deleting a policy behind an anchor, and conflicting at the same revision were rejected.
- A synthetic Credential Manager write failure after the signed policy file committed left the old anchor intact; reopen accepted exactly the next predecessor-linked revision, advanced the anchor and rejected wider jumps.
- A transfer interrupted after its first 64 KiB chunk resumed at a nonzero offset and downloaded fewer bytes on retry than the full release.
- Lazy 150,000-byte artifact retrieval, deprecation, offline search and explicit rollback passed.
- Publication retry used reordered equivalent changes and a later timestamp after repository reopen, returned the byte-identical original descriptor and kept the chain at sequence 1; a changed request under the same operation ID was rejected.
- Hostile filesystem fixtures rejected linked application/server ancestors, filesystem roots, swapped staging/artifact directories, linked active-pointer/database files and digest-path metadata substitution without writing through the links. A P-384 server key was rejected before repository use.
- Two client objects sharing one replica serialized a deliberately blocked release transfer. The first applied sequence 1; the second then reported already current, cancellation while queued was prompt, no partial remained, and later access succeeded. A separately held no-share file handle proved cross-process-style contention/cancellation/recovery. A linked operation-lock fixture was rejected without changing its external target.
- Native UIA started from release 0, invoked one explicit signed synchronization, exposed release 1 and two resources, returned one exact CCB from offline search, read its metadata, reported zero network requests/private collections and observed zero fixture sockets. The normal main-shell route kept search/sync disabled and announced that no remote or empty-catalog state was inferred.
- Enrollment tests rejected remote HTTP, P-384, linked, malformed, digest-conflicting and noncanonical candidates. A unique-target real Windows Credential Manager round trip required approval, rejected a stale review, replaced one exact root/origin and cleared idempotently.
- Source-composition tests proved unconfigured open creates neither storage nor a network handler. Configured open/status/search issued zero requests; explicit synchronization issued the first request to the exact enrolled origin, and close disposed its HTTP lifetime.
- Enrollment UIA proved heading-first focus; seven unique access keys; initially disabled/unchecked approval; exact P-256 fingerprint/address review; full-path minimization; review revocation on candidate change; explicit save/remove interlocks; zero sockets; the production Credential Manager/HTTPS boundary; and focus return after close. The unconfigured shell used an isolated store, so the gate could not read or overwrite real enrollment.
- The first UIA attempt exposed a CLR type name instead of the resource label. `GlobalResourceListItem.ToString()` now returns the human-readable display string; the unchanged exact assertion passed afterward.
- Latest local evidence: `artifacts/test-results/p09-f1f931e9e77f4895a560a29904df8954/` containing `p09-global-knowledge.trx`, `storage-forecast.json` and `global-knowledge-uia.json`, generated from implementation commit `65d9718`; full-suite TRX: `artifacts/test-results/p00-tests.trx`; server capability evidence: `artifacts/test-results/ubuntu-audit-2e50bf228e12483585cea19ebf05462a/` (generated and intentionally not committed).

## Measured storage/bandwidth fixture

The deterministic benchmark uses 1,000 resources and excludes artifact bodies:

| Fixture | Bytes |
|---|---:|
| Metadata-only canonical catalog | 470,703 |
| Initial signed release | 722,679 |
| Catalog with 384-dimensional vectors | 4,388,562 |
| Signed delta changing 100 resources | 73,559 |
| Two retained metadata catalogs plus two releases | 1,738,544 |

Artifact storage and transfer must add exact signed artifact sizes. Artifacts are not included in catalog sync and download only on use.

## Changed files in the latest milestone

- Protected enrollment record/reviewer/store, exact endpoint policy and offline-first source composition: `src/DCx.Core/GlobalKnowledge/GlobalRegistryEnrollment.cs`, `src/DCx.Core/GlobalKnowledge/GlobalKnowledgeSourceFactory.cs`, `src/DCx.Core/GlobalKnowledge/HttpGlobalRegistryTransport.cs`, `src/DCx.Core/GlobalKnowledge/GlobalKnowledgeViewSource.cs`.
- Accessible review/approve/replace/clear UI, normal shell composition and isolated fixtures: `src/DCx.Desktop/GlobalRegistryEnrollmentWindow.xaml`, `src/DCx.Desktop/GlobalRegistryEnrollmentWindow.xaml.cs`, `src/DCx.Desktop/GlobalKnowledgeWindow.xaml`, `src/DCx.Desktop/GlobalKnowledgeWindow.xaml.cs`, `src/DCx.Desktop/MainWindow.xaml.cs`, `src/DCx.Desktop/App.xaml.cs`.
- Exact-root/vault/source tests and expanded native UIA gate: `tests/DCx.Core.Tests/GlobalKnowledge/GlobalRegistryEnrollmentTests.cs`, `scripts/validate-p09-global-ui.ps1`.
- Decision, security/privacy boundary and human checklist: `docs/architecture/adr/0093-protected-explicit-global-registry-enrollment.md`, `docs/security/THREAT_MODEL.md`, `docs/privacy/PRIVACY_MODEL.md`, `docs/accessibility/P09_GLOBAL_KNOWLEDGE_CHECKLIST.md`, `docs/DECISIONS.md`.

## Blocker and limits

- Read-only evidence from `artifacts/test-results/ubuntu-audit-2e50bf228e12483585cea19ebf05462a/` confirms the owner's server is Ubuntu 24.04 x64 with Docker 29.5.2, Compose 5.1.4 and an active daemon. The authenticated UID 1000 is not in the `docker` group, so daemon access is denied. No remote DCx deployment exists.
- Consequently, `server/docker-compose.simulation.yml` remains unexecuted and P09 is not complete. Granting Docker/sudo access or deploying/building on the real server is privileged/external mutation and requires explicit owner approval. After approval, validate Compose configuration, record image digests, run the same two-client gate against the container, and update this phase.
- Simulation signing keys and the offline root are ephemeral fixture material. The per-user Windows anchor mitigates replica-file rollback but is deletable by same-user code and is not TPM/hardware monotonic storage. Production offline-root custody/ceremony, anchor loss/recovery, TLS identity, backup/restore, monitoring and external security review are not implemented.
- The product can now bind one exact user-reviewed root/origin, but no production root, origin, certificate, fingerprint-verification ceremony or server exists. Enrollment is not deployment evidence or permission to use a simulation key in production.
- The replica operation lease closes cooperating client/process mutation races on one replica. Reparse and exact-path checks still cannot make every separate check/open operation race-free against malicious code already running as the same user. Production requires a dedicated least-privilege identity plus approved ACL, mount, secret-file and container policies.
- The service exposes read-only catalog endpoints by default. P10 adds a separate default-off, loopback/container simulation contribution API; this is not authorization or infrastructure for production exposure.
- Real Ubuntu deployment remains forbidden without explicit owner approval.
- Human JAWS/NVDA/Narrator acceptance for the new window remains unverified; automated UIA does not satisfy that gate.
