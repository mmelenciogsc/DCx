# P10 — Contributions, governance and provenance

- Status: BLOCKED (authenticated local-simulation intake/review and server-side CCB hostile-code policy pass; production publication remains unavailable)
- Partial implementation commits: `4016c2c`, `ce8d4de`, `adbb511`, `00fedd6`, `9123a81`, `5c3611e`, `bd30319`, `f417f6e`, `0fc2649`, `bbadbca`, `e13246b`
- Documentation commit: recorded by the commit containing this file

No contribution was sent externally or published to a production catalog.

## Implemented

- Accessible Windows contribution-review surface with global preparation opt-in initially unchecked, three unchecked attestations, unchecked digest-bound item approval, disabled preparation action and explicit no-upload status.
- Editing the item clears review and approval. Prepared items always begin in local quarantine; the UI contains no upload/publish action.
- Canonical item digest covers body digest, resource identity/version, source, license, contributor pseudonym, artifact digest, capabilities and derivative credits.
- Client and authoritative rescans for common private keys/API credentials, email/phone/government ID, suspicious shell patterns, dummy/empty material, size, license and artifact-policy findings. Finding records contain codes, never matched values.
- Ownership/share-rights, license and sensitive-data attestations plus a 30-minute, nonce-bearing, exact-item approval.
- SQLite quarantine with unique approval nonces, idempotent submission identity, reviewer transition, and CKB propagation into the signed P09 release chain only after a clean authoritative rescan.
- Withdrawal publishes a signed deprecation.
- Derivative credit graph rejects duplicate/invalid weights, allocation above 100% and cycles.
- Minimal usage-receipt contract excludes prompts, bodies, paths and URLs; HMAC integrity, receipt/nonce replay denial, entitlement/global-resource checks, self-use exclusion, success evidence, time window and daily anomaly cap.
- Server-side Linux sandbox contract and versioned evidence bind artifact, canonical extracted workspace, test suite, policy launcher, Bubblewrap backend, static runtime and output digests.
- Rootless Bubblewrap policy creates new user, mount, PID, network, IPC, UTS and cgroup namespaces; drops every capability; clears the environment; uses UID/GID 65534; and exposes only a read-only package, read-only request, pinned static BusyBox, minimal `/proc`/`/dev`, and private tmpfs work areas.
- The empty root is remounted read-only. No host home, repository, system tree, socket, D-Bus endpoint or external network route is mounted.
- Canonical workspace admission rejects traversal, unsupported names, links/special files, more than 256 entries, more than 64 MiB, digest substitution and requests over 1 MiB.
- CPU, address-space, process, descriptor, output-file, diagnostic, wall-clock and captured-output bounds fail closed. Child diagnostics are never retained.
- The production executor rehashes Bash, the policy launcher, Bubblewrap and runtime before every execution, uses argument arrays and a cleared environment, kills the process tree on cancellation, and removes per-operation input storage.
- Contribution CCB schema v2 is an exact five-file profile: manifest, BusyBox-shell entrypoint, deterministic tests, CycloneDX SBOM and license. Unknown manifest/test fields, duplicate JSON properties, undeclared ZIP members, path aliases/collisions, non-UTF-8 or null-bearing text and unclosed runtime dependencies fail admission. Existing P08 schema v1 remains compatible.
- Candidate identity/version/license are cross-checked against the package and SBOM. All package text and test inputs receive authoritative secret/PII/suspicious-command scanning; scan storage contains codes only.
- Accepted ZIPs enter a digest-addressed quarantine. Approval rereads and rehashes the exact artifact, safely extracts only admitted files into an ephemeral workspace, recomputes its canonical digest and sends every declared test to the injected server-side sandbox executor.
- Each result must cryptographically bind policy, artifact, workspace, test suite, launcher, backend, runtime and output. Minimized evidence hashes/codes are persisted without request or output bodies. A failed test, output mismatch, changed package, forged binding or missing executor prevents publication.
- Only after every test passes is the original digest-verified ZIP added to the P09 artifact store and referenced by a signed global release. A replica test verifies signed synchronization and lazy byte-identical artifact retrieval.
- Approval and withdrawal use digest-derived stable publication-operation IDs. The registry atomically binds each ID to the canonical change set and release; a retry after the registry commit but before governance-state update returns the original release instead of appending a duplicate.
- Reviewer-authorized approval/withdrawal intents are now persisted before registry mutation. Startup validates and processes a bounded batch, then commits the contribution transition and intent completion together. Recovery reuses the original reviewer, timestamp and change digest; no new consent or publication scope is inferred.
- Candidate JSON is rehashed against its intake digest before approval, withdrawal and recovery. CCB recovery rehashes both quarantine and authoritative artifact bytes and can complete from already-persisted evidence without rerunning untrusted code.
- A separate SQLite row atomically claims each pending intent with a random per-process owner, random fencing token, acquisition/expiry times and bounded attempt count. The lease coordinates execution only; the immutable reviewer intent remains the sole publication authority.
- Five-minute leases are renewed before and after registry work. Completion rechecks owner, token and unexpired time inside the same transaction as the contribution/intent transition; an expired worker cannot complete after another process reclaims the intent.
- Successful completion clears the active lease but retains its attempt count. Crashes leave it to expire, ordinary failures release it, malformed rows fail closed, and the twentieth failed claim produces a stable attempt-limit error rather than an unbounded loop.
- A versioned binary contribution envelope carries strict JSON metadata plus an optional raw artifact. Unknown metadata fields fail admission; metadata is capped at 1 MiB, artifacts at 32 MiB, and Kestrel and endpoint readers enforce the same total bound and exact declared length.
- A default-off simulation API exposes authenticated intake, owner-scoped status and withdrawal, reviewer-scoped pending/status and approval endpoints only when `DCX_GLOBAL_CONTRIBUTION_API=1` and all role settings pass startup validation.
- Contributor and reviewer bearer credentials are configured only as distinct SHA-256 digests. Role and pseudonymous identity come from server configuration, comparisons are constant-time, transient token buffers are zeroed, and errors contain stable policy codes rather than credentials or request bodies.
- Every enabled contribution route is covered by a process-local fixed-window limiter before endpoint authorization/body work. Defaults are 120 requests/minute and four concurrent requests per canonical remote address; startup rejects configured values outside 10–10,000 and 1–32. A maximum 1,024 SHA-256 address partitions bounds memory; raw addresses and bearer tokens are neither retained as keys nor exposed by errors.
- Rate, concurrency, partition-capacity and invalid-clock denials return `429` with one stable body-free code. Window state rejects non-UTC/regressed timestamps, expired inactive partitions are reclaimed, and an idempotent request lease releases concurrency even when the endpoint fails.
- A CKB can propagate only after authoritative rescan and reviewer approval. Raw CCB packages can enter quarantine, but approval fails closed unless the explicit Linux sandbox configuration and every hash-pinned executable are present.
- The service admits only a bounded regular non-reparse signing-key file, reads it without write sharing and zeroes the private-key byte buffer immediately after repository initialization.
- A separately gated hosted worker continuously polls a bounded ready-intent batch. It cannot start unless the contribution API is explicitly enabled; the poll interval is constrained to 100–60,000 ms, and health reports worker activation separately.
- Worker failures persist only operation/submission identifiers, a stable code, bounded count and timestamps. Exponential delays cap at five minutes; five consecutive transient failures or one permanent policy failure enters dead-letter state. Candidate bodies, exception messages and stack traces are structurally absent.
- Reviewer-only queue/status and retry routes expose the minimized state. Retry clears dead-letter scheduling but cannot alter the immutable reviewer intent, canonical change digest, candidate, contributor, reviewer or publication operation identifier. Successful completion marks any failure record resolved in the same governance transaction.
- API CCB approval now requires the worker and first persists a deterministic job binding submission, candidate digest, server-derived reviewer and request time. The job and its reviewer-only queue contain no candidate or artifact body.
- A separate 30-minute ownership-fenced review lease is acquired before package extraction or sandbox execution. Its duration exceeds the current closed profile's maximum 32 tests at 30 seconds each. Same-SQLite concurrent service instances executed one claimed package exactly once in the deterministic fixture.
- CCB review failures use the same bounded backoff/dead-letter principles; malformed leases/rows fail closed and 20 expired crash claims dead-letter. Explicit reviewer remediation resets the claim budget without changing the job binding. Only a successfully reviewed job can enter the existing signed publication-intent path.

## Reproduction

```bash
cd /mnt/d/DCx
powershell.exe -NoProfile -ExecutionPolicy Bypass -File D:\\DCx\\scripts\\validate-p10.ps1
powershell.exe -NoProfile -ExecutionPolicy Bypass -File D:\\DCx\\scripts\\ci.ps1
```

## Measured result

- Locked native Release build: 0 warnings, 0 errors; 1.29 seconds in the recorded dedicated run.
- P10 tests: passed 40, failed 0, skipped 0; about 9 seconds in the recorded native gate.
- Current full suite after P06 digest-bound inference hardening: passed 201, failed 0, skipped 0; about 8 seconds.
- Eligible CKB item: quarantined, absent from catalog before review, approved into a signed release, synchronized by a client, then withdrawn/deprecated.
- Secret/PII/dummy item: remained quarantined and reviewer publication failed closed.
- Receipt: one qualified event; replay, contributor self-use and invalid integrity excluded.
- Native UIA: all five consent/attestation controls initially unchecked, preparation disabled, and no-upload state accessible.
- WSL-hosted Ubuntu local simulation: Bubblewrap 0.6.1 SHA-256 `bf6cf3d4456665f5d80c14eb79a76e0ce87b39a0883e614a161658b4ec33492c`; static BusyBox SHA-256 `b3c1009e1b5c927e537487c80639cdf404f69e3eb49371d9be5d841672be3ff9`.
- Benign transformation passed. Host/repository and `/etc`/`/usr` reads, an external network request, root write, inherited secret environment, outside-quarantine workspace, symlink, workspace-digest substitution, timeout and output overflow were denied.
- Latest hostile-sandbox evidence: `artifacts/test-results/p10-46e523d1f4d6471991f66a10242149ae/sandbox-evidence.json` (generated and intentionally not committed).
- Schema-v2 success path executed two declared tests, persisted two passing evidence rows, published the exact artifact into a signed release, synchronized it to a separate global replica and lazily retrieved byte-identical content.
- Denial coverage includes missing executor, package/digest/metadata mismatch, undeclared files/fields, invalid UTF-8/base64/null input, malicious entrypoint or test-input text, expected-output mismatch and post-intake quarantine tampering.
- Cross-database crash simulations pre-commit approval and withdrawal releases, then verify governance retries reuse sequences 1 and 2 and synchronize one correctly deprecated resource rather than producing duplicate releases.
- Deliberate crash checkpoints now cover intent-before-registry and registry-before-local-completion windows. Reopen automatically completes approval and withdrawal, while CCB recovery executes zero additional sandbox tests. Changed intent digests, changed post-review candidate JSON and same-length authoritative artifact substitution all fail closed.
- Two concurrent governance/repository instances claimed one pending approval exactly once. An unexpired lease blocked takeover; after forced expiry a second worker reused the idempotent sequence-1 registry operation and completed once, while the original worker was fenced with `publication_lease_lost`.
- Expired malformed lease data failed closed before overwrite. Attempt 20 stopped with `publication_lease_attempt_limit`. Successful completion retained attempt audit count and cleared every active lease token.
- A real child Kestrel process denied missing, invalid and wrong-role credentials; denied a contributor-identity mismatch; admitted an exact approved CKB idempotently; published it once; synchronized it to a fresh replica; and rejected approval replay.
- The same process kept a credential/PII-bearing candidate quarantined, admitted a raw CCB but denied approval without the configured sandbox, denied reviewer withdrawal, accepted owner withdrawal and propagated the signed deprecation.
- Captured child-process diagnostics contained neither bearer credentials nor the private or malicious contribution bodies. The API remained absent (`404`) under the default configuration.
- A real hosted loop completed an authorized intent left pending across service reconstruction. Persistent registry conflict produced scheduled exponential retries, deferred early polls, dead-lettering on failure five, reviewer-only explicit rescheduling and fail-closed rejection of malformed persisted queue state.
- The queue JSON excluded the reviewed private body. A child process exposed worker health only when both simulation gates were enabled and failed startup when the worker was requested without the contribution API.
- CCB approval returned a durable queue job before executing zero sandbox tests; the worker later executed both declared tests and published once. Two concurrent governance instances claimed that work once, with two total test executions rather than four.
- Missing worker prevented API-path CCB approval. Output mismatch dead-lettered with a stable code and body-free status; reviewer retry was explicit. Malformed review leases/rows failed closed, and a 20-crash attempt limit required remediation before successful review.
- Persisted scanner codes containing the policy namespace separator (`.`) are now admitted and exposed as codes; credential and PII values remain absent.
- A real child Kestrel process returned `401` for the first ten missing-credential requests and then a stable `rate_limited` `429` for the eleventh request in the same minute. Deterministic unit coverage also verifies concurrency denial, idempotent release, per-address isolation, window reset and fail-closed non-UTC/regressed time. Captured diagnostics contained neither configured bearer credential.

## Changed files in the latest milestone

- Bounded transport codec: `src/DCx.Core/Contributions/ContributionTransportCodec.cs`.
- Default-off authenticated endpoints and optional hash-pinned sandbox configuration: `server/DCx.GlobalService/SimulationContributionApi.cs`.
- Bounded process-local request/concurrency policy: `server/DCx.GlobalService/SimulationContributionRateLimiter.cs`.
- Governance status/pending queries and owner-bound withdrawal: `server/DCx.GlobalService/ContributionGovernanceService.cs`.
- Service wiring, request bound and private-key admission/zeroing: `server/DCx.GlobalService/Program.cs`.
- Hosted polling and fail-closed configuration: `server/DCx.GlobalService/ContributionPublicationWorker.cs`.
- Durable pre-sandbox CCB jobs, leases, status and remediation: `server/DCx.GlobalService/ContributionCcbReviewQueue.cs`.
- Child-process, limiter and codec/governance coverage: `tests/DCx.Core.Tests/Contributions/ContributionApiE2eTests.cs`, `tests/DCx.Core.Tests/Contributions/SimulationContributionRateLimiterTests.cs`, `tests/DCx.Core.Tests/Contributions/ContributionPolicyTests.cs`, `tests/DCx.Core.Tests/Contributions/ContributionGovernanceTests.cs`, `tests/DCx.Core.Tests/GlobalKnowledge/GlobalServiceE2eTests.cs`.

## Blocker and limits

- The new API is a loopback/container simulation using static hash-configured bearer roles. Its limiter is in-memory, resets on restart, partitions the direct peer rather than trusted-proxy identity, and is neither shared nor coordinated across nodes. It is not a production identity/ingress system: there is no TLS termination, OAuth/OIDC or mTLS identity, credential rotation/revocation lifecycle, distributed ingress throttling, public ingress policy, administrative audit surface or account recovery.
- There is intentionally still no Windows-client upload action. Local preparation remains no-upload until an approved deployment, production identity design and accessible human review flow exist.
- The current runtime profile accepts only exact, bounded BusyBox-shell packages with a two-component closed SBOM. It has no language-specific reproducible builder images or general dependency-lock resolver.
- This local WSL run is development evidence, not execution on the owner's Ubuntu 24.04 server. WSL is not and will not be an end-user requirement.
- The prototype has no seccomp syscall profile, cgroup-v2 enforcement, immutable OCI/base-image digest, service-account deployment hardening, kernel/LTS policy, production monitoring or independent penetration test. Bubblewrap itself explicitly requires the caller to define a complete policy; this milestone does not claim production-grade hostile isolation.
- Docker/Compose remains unavailable as recorded in P09. The API wiring and real Bubblewrap executor have each passed independently, but no Linux child-service run has exercised the HTTP-to-CCB-sandbox path because a Linux .NET service runtime/approved Compose environment is unavailable. Production publication must remain unavailable until approved Ubuntu 24.04 deployment, production-grade identity and the remaining isolation/external security gates pass.
- Regex scanners are defense-in-depth, not a guarantee that content lacks secrets/PII or that the contributor owns it. Human/policy review remains mandatory.
- An HMAC proves receipt integrity/installation association, not that an untrusted customer device reported honest usage. Server anomaly analysis, audit sampling and entitlement/account correlation remain required.
- Each worker cycle processes at most 100 ready intents. Same-SQLite local processes coordinate with fencing leases and persist retry/dead-letter state, but there is no supported multi-node/network-filesystem deployment, production scheduler, alert transport, operator UI, retention policy, SLO or high-availability evidence.
- The authenticated API path now claims a durable CCB job before hostile execution. The older direct governance method remains for in-process tests and can bypass that queue, so it must not become a production endpoint. The queue is same-SQLite simulation infrastructure, not a supported distributed broker; its fixed lease depends on the current bounded test profile and has no production heartbeat/alerting evidence.
- Human JAWS/NVDA/Narrator testing remains unverified; see `docs/accessibility/P10_CONTRIBUTION_CHECKLIST.md`.
