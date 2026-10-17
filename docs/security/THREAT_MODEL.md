# Threat model

## Protected assets

User files and prompts; browser sessions; API/OAuth/PayPal credentials; private CKB/CCB; Windows input/focus; local process authority; global signing keys and catalog; contribution provenance; usage receipts; entitlement and CARE ledgers; payout approvals; audit evidence.

## Trust boundaries

| Boundary | Untrusted side | Trusted enforcement |
|---|---|---|
| WebView2 content ↔ client | Web pages, scripts, downloads, DOM text | Origin-aware browser adapter and download quarantine |
| Documents/media ↔ parsers | Files, URLs, embedded objects, OCR text | Type/size limits, sandboxing, safe parsers, output validation |
| Model output ↔ tools | Prompt injection, hallucinated arguments | Typed schemas, policy engine, capability grants, evidence checks |
| UI ↔ Windows broker | Agent action requests | Allowlists, safe argument binding, focus lease, approval/cancel |
| Client ↔ OpenAI | Optional approved task payload | Redaction, minimization, protected key storage, TLS, cancellation |
| Client ↔ global service | Catalog updates/contributions/receipts | Signatures, nonce/idempotency, minimization, quarantine |
| Global service ↔ PayPal | Webhooks, entitlements, payouts | Signature verification, reconciliation, owner approval, sandbox first |

## Priority threats and required controls

1. **Prompt injection causes an OS or financial side effect.** Content-derived instructions have no capabilities. The policy engine requires a typed user-intent grant; destructive, external, privileged, publishing, and financial actions require accessible confirmation.
2. **Focus theft or conflicting input harms the user.** A single control coordinator issues revocable agent focus leases. Any user input pauses agent input. Handback restores a recorded UIA element when safe and otherwise announces a deterministic fallback.
3. **Browser session theft.** WebView2 profiles are DCx-owned, ACL-protected, and never created from Chrome cookies. Secrets are excluded from logs, artifacts, screenshots, crash reports, and model context.
4. **Command injection.** Brokers use executable plus argument-array contracts, canonical paths, workspace scopes, time/output limits, and deny shell interpolation by default.
5. **Malicious dependencies or community code.** Pin packages, record hashes/licenses/SBOM, scan, build in quarantine, test permissions, sign approved artifacts, support revocation.
6. **Tampered global updates.** Verify a pinned trust root, manifest signature, artifact digest, monotonic version, expiry/revocation, and atomic swap; preserve rollback.
7. **Private-data exfiltration.** Upload defaults off. Contributions require item-level preview, ownership attestation, redaction and secret/PII/license scans. Telemetry excludes prompts and source content.
8. **Receipt/payout fraud.** Entitlement-bound signed receipts, replay prevention, anomaly checks, audit sampling, immutable journal semantics, double-entry reconciliation, holds, and final owner approval.
9. **Local denial of service/cost runaway.** Per-task token, time, memory, process, network and retry budgets; model unload; cancel/kill only the scoped child process.
10. **Confused-deputy elevation.** The main app runs unelevated. A future elevation helper must be narrowly scoped, short-lived, separately authenticated, and invoked only after explicit consent.

## Implemented OpenAI/Codex controls through P04

- The child environment is rebuilt from a small OS-variable allowlist; arbitrary parent secrets are not inherited.
- DCx uses an isolated per-user `CODEX_HOME` and ephemeral app-server auth storage, so it does not copy or silently reuse the developer's Codex/ChatGPT session.
- API keys live in Windows Credential Manager, never command arguments or project files. Known values and bearer credentials are redacted from bounded child diagnostics.
- P04 threads are ephemeral and read-only; web search, apps and multi-agent features are disabled. Unhandled server tool requests receive no authority.
- Commercial ChatGPT-plan sign-in remains compiled unavailable until eligibility is evidenced. A model catalog is never considered authorization.

## Implemented BOSS controls through P05

- Task graphs reject duplicate/unknown dependencies and cycles before execution.
- Routing is capability- and privacy-constrained with deterministic-first precedence; unavailable private tasks do not silently route to OpenAI.
- Evidence, attempt, elapsed-time, task-token and aggregate-token gates fail closed. The aggregate breaker prevents later capability execution after overrun.
- Checkpoints record decision metadata and measurements but omit task prompt/output bodies.
- Capability exceptions are classified without persisting exception messages that could contain private content.

## Implemented local-model controls through partial P06

- Ollama owns a loopback-root-only HTTP transport with redirects, cookies and proxies disabled. Every response must remain on the exact approved scheme/host/port. Model names, prompts, admitted PNG/JPEG/WebP images, embedding batches, vector dimensions and JSON responses are bounded; mismatched/incomplete vision results and inconsistent/nonfinite vectors fail closed.
- Model capability and license metadata are inspected locally. Selection respects measured VRAM/RAM budgets and uses deterministic least-resource embedding preference.
- Remote fallback requires connectivity plus separate provider approval and is unavailable for private input when no local backend exists.
- Whisper executable and model files are SHA-256 pinned and reverified for every run. Verified files remain held by read-only, no-write/no-delete leases through process completion so they cannot be replaced between verification and use. Only regular signature-checked WAV input is accepted.
- Whisper runs without shell interpolation, with a scrubbed environment, typed arguments, timeout/tree kill, transcript size/reparse checks and deleted operation storage. Child diagnostics are drained but never retained.
- Current Whisper and embedding successes are deterministic adapter fixtures, not accuracy or installed-backend evidence. P06 remains blocked pending approved binaries/models and real quality/resource benchmarks.

## Implemented content controls through P07

- Local admission canonicalizes paths, rejects direct reparse files, applies per-kind size ceilings and verifies binary signatures before viewer/parser dispatch.
- Office ZIP/XML extraction caps entry count and expanded data, prohibits DTDs and external XML resolution, and never executes embedded code or objects.
- OCR-derived geometry and repaired semantics must validate before export; display-only content is not silently converted into an editable field.
- YouTube admission requires HTTPS, an exact supported host and a valid video identifier; credentials, lookalikes and unsupported page types are denied.
- Media does not autoplay. FFmpeg receives typed arguments and a bounded timeout; temporary frames are removed. Frame descriptions use only loopback Ollama and leave playback paused for user-controlled resume.
- HTML/DOCX exports are newly generated semantic artifacts rather than modified untrusted source packages. Human review remains necessary before relying on OCR or model descriptions.

## Implemented private-knowledge controls through P08

- Private profile identifiers map to distinct hashed directories; databases, vectors and artifacts are never queried across those roots.
- Optimistic version checks and SQLite transactions prevent silent lost updates. Resource bodies and vectors are bounded and validated before insertion.
- Content-addressed writes verify declared SHA-256 and use same-directory atomic moves. CCB reads repeat artifact integrity, package identity and provenance checks.
- CCB ZIPs reject traversal, absolute/backslash paths, duplicates, symbolic links, excessive entry/expanded size, malformed manifests and missing entrypoints.
- CCB publisher trust is pinned by subject-public-key info. Canonical ECDSA signatures bind code digest and meaningful metadata; an untrusted publisher, changed body or changed package fails closed.
- Artifact packages are data, not executable authority. Only a host-registered adapter with matching identity/digest and passing bounded fixtures becomes a BOSS capability; package code is not dynamically loaded.
- FTS input is tokenized into a bounded parameterized expression. All relational writes and queries use parameters rather than concatenated user content.

## Implemented global-replica controls through partial P09

- Every canonical release is ECDSA-signed and binds its sequence, previous signed-release SHA-256 and resulting canonical-catalog SHA-256.
- Clients stage bounded chunks, preserve interrupted partials, verify descriptor digest/signature/chain/result, write immutable generation files and atomically replace only the active pointer.
- Wrong signatures and conflicting sequences fail before activation. Prior signed generations support explicit rollback; hardware-backed anti-rollback is not claimed.
- A pinned offline P-256 root signs canonical trust-policy snapshots. Policies bind monotonically chained revisions and release-key SHA-256 identities, sequence windows and revocation points; a fresh client downloads every retained policy revision before accepting releases.
- Trust-policy history is durable in the authoritative SQLite registry and transferred in bounded digest-checked chunks. Stale/conflicting policy state, a non-root policy signature and a revoked release signer fail closed.
- A rollback target is verified under the current trust policy before pointer replacement, so an emergency revocation cannot corrupt the current active pointer. Production root custody and an OS/hardware-protected minimum revision remain unresolved.
- Global replicas are keyed by trust-root hash under a separate `global` directory and never receive a private-store handle. Two-client tests preserve a private sentinel.
- Artifact metadata is signed with the catalog. Bodies remain lazy, content-addressed and size/digest verified; authoritative publication rejects missing artifact records.
- The HTTP client requires HTTPS except for literal loopback HTTP. The simulation server refuses startup without its explicit simulation flag, data root and external key file.
- Container context excludes secrets and runtime data; the Compose definition is read-only/non-root/no-new-privileges. It remains unexecuted because Docker access is blocked.

## Implemented contribution controls through partial P10

- Contribution preparation defaults off, and each changed item invalidates its prior review/approval digest. Local preparation always yields quarantine and no UI/network upload action exists.
- Rights/license/sensitive-data attestations, bounded approval age and unique nonces are revalidated at authoritative intake; client-provided scan results are not trusted.
- Scanner findings store only stable codes, not matched secrets or PII. Risky/dummy content cannot transition to approved.
- Approved CKB metadata enters the signed release path; withdrawal emits deprecation. The local server fixture permits a CCB transition only after exact package admission and passing authoritative sandbox evidence; the HTTP service exposes no contribution endpoint.
- A server-side rootless Bubblewrap prototype now executes hostile BusyBox-shell fixtures in separate user/mount/PID/network/IPC/UTS/cgroup namespaces. It drops all capabilities, clears environment, runs as 65534, exposes only digest-bound read-only package/input/runtime mounts, remounts root read-only and applies bounded CPU/address-space/process/file/descriptor/output/wall limits.
- Sandbox evidence binds the claimed artifact, canonical extracted workspace, test suite, policy launcher, Bubblewrap, runtime and output digests. Outside-quarantine paths, links/special files, digest substitution, host/system reads, external network, root writes, inherited secrets, timeouts and output floods fail closed in the local hostile gate.
- Exact schema-v2 package intake uses digest-addressed quarantine, identity/version/license and two-component CycloneDX/runtime closure, safe extraction, rescanning of every textual component and test input, all-test execution, durable minimized evidence, expected-output verification and signed artifact propagation. Tampering or a missing executor fails closed.
- Approval and withdrawal persist immutable reviewer-authorized intents before registry mutation. The registry atomically binds the digest-derived operation ID to a canonical change-set digest and release; startup validates a bounded batch and atomically advances local state/intent completion without duplicate releases. Recovery rehashes candidate and artifact data, while changed intent/candidate/artifact bytes fail closed.
- This is not yet a production publication boundary: there are no authenticated upload/reviewer endpoints, durable outbox/worker leases, general dependency-lock/build profiles, seccomp, cgroup-v2 measurement, immutable builder image, Ubuntu 24.04 deployment, monitoring or independent penetration test. The simulation HTTP service remains read-only.
- Credit lineage rejects cycles, duplicate parents, invalid weights and total parent credit above 100%.
- Usage receipts contain identifiers, time and verification status only. Integrity, replay, entitlement, approved-resource, self-use, success, time and rate checks fail closed; client honesty is still treated as an untrusted signal.

## Implemented finance controls through partial P11

- Money uses checked integer cents. Journal entries must balance and conflicting event IDs fail closed.
- The defined contributor pool is zero for nonpositive net and rejected above 40% aggregate; allocation conserves exact cents deterministically.
- Sandbox webhooks require fixture integrity and unique transmission/event IDs. Refund/reversal suspends entitlement and posts a correcting balanced entry.
- Payout execution requires sandbox mode, verified eligibility, explicit owner approval, Net 60 and a unique batch. Live mode has no provider path and always fails.
- SQLite persists canonical event hashes, postings, webhook replay identifiers, entitlement and payout terminal state with WAL and full synchronous writes. Reopen tests prove local idempotency and conflicting-event denial.
- Reserved payout batches retain canonical previews and SHA-256 digests. Restart reconciliation first looks up the stable batch ID: accepted/failed batches become terminal without resend, provider-pending batches remain reserved, and provider-not-found batches are resent idempotently with the same ID.
- These controls remain local mocks. Official PayPal webhook authenticity, provider lookup/status semantics and idempotency still require sandbox acceptance; account/recipient eligibility, key custody and legal/accounting policy remain blockers.

## Implemented release/recovery controls through partial P13

- Offline startup uses an embedded semantic document, permits its data navigation only as the first app-controlled navigation, and performs no website request.
- Backup scope is allowlisted to browser metadata and private knowledge. WebView2 profiles/cookies and Windows protected secrets are excluded.
- Backup and restore reject filesystem roots, reparse points, archives inside protected data, traversal/absolute/ADS paths, duplicates and size/count excess. SHA-256 and length are checked before any current data is replaced.
- Normal DCx holds an exclusive file lease scoped to its canonical data root. The separate recovery mode must acquire that lease for the whole backup or restore, preventing live SQLite/WAL copying and concurrent recovery operations.
- Restore requires a default-unchecked UI acknowledgment, stages content, and retains/moves prior roots for rollback. A denied operation does not create an archive or replace current data.
- Two publish trees and ZIPs are compared by path/length/digest. Published files receive binary secret-pattern scanning. Synthetic hostile-input fixtures are allowlisted only in their two source test files.
- Crash capture is versioned-consent/default-off and records closed categories rather than exception content. Its six-field schema excludes messages, stacks, dumps, prompts, URLs, paths and identities; strict bounded atomic files remain local and retain at most 20.
- UI, task and process exception hooks do not mark exceptions handled or alter runtime failure semantics. Recording failure is swallowed only at the optional diagnostic boundary so it cannot prevent normal startup or mask the original crash.
- Accessible deletion preserves the current setting, while opt-out purges retained reports. No upload/export transport exists; local consent cannot authorize a future transfer.
- These controls do not replace code signing, AV/vulnerability scans, clean-VM lifecycle testing or human assistive-technology acceptance.

## Implemented readiness controls through partial P14

- A `GO` decision requires zero nonvalidated critical/major issues and a separate explicit production-activation approval. Approval cannot override a blocker.
- Readiness entries fail closed without a unique ID, owner role, next action and evidence reference. The current report assigns all seven critical/major blockers and remains `NO_GO`.
- The JSON report and native dashboard consume the same immutable current-readiness policy. The dashboard is read-only and exposes no production-activation, enrollment, collection, publishing or payment action.
- Pilot metric storage defaults off and accepts only the 12 reviewed keys, closed enum dimensions and bounded numeric samples. SQLite stores daily count/sum/min/max aggregates, not raw events or user/content fields.
- Versioned consent cannot roll back. Opt-out atomically disables recording and purges the local aggregates. No product event source or network transport is wired; collection still requires an approved notice, accessible enrollment/withdrawal, retention and incident procedures.
- Incident stop conditions include credential/private-data exposure, unauthorized side effects, reconciliation variance, signing failure, inaccessible critical flows and user-control safety failures.

## Security gate policy

Security tests must cover denial paths as well as successful paths. A mock or sandbox pass proves only that path. Production OpenAI/PayPal eligibility, code signing, legal review, external penetration testing, and the Ubuntu production deployment remain explicit blockers until separately evidenced.
