# Reproducible development and build

## Required Windows tooling

- Windows 11 x64.
- .NET SDK 10.0.401 (pinned by `global.json`).
- Microsoft Edge WebView2 Runtime.
- Network access to the single configured NuGet source for first restore.

Visual Studio is not required for the current command-line build. WSL is optional and not used at product runtime.

## Native Windows gate

From PowerShell 7.4 or newer on .NET 10:

```powershell
Set-Location D:\DCx
dotnet restore DCx.slnx --locked-mode
dotnet build DCx.slnx --configuration Release --no-restore
dotnet test DCx.slnx --configuration Release --no-build
pwsh.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\validate-p13.ps1
```

From the current WSL development environment:

```bash
cd /mnt/d/DCx
./scripts/verify-p00.sh
```

Build outputs are under each project's `bin/Release` directory and are ignored by Git. Do not commit credentials, local browser profiles, package caches, signing keys, or test-result artifacts.

## Local readiness and metric privacy controls

Open **Release readiness** from the main window or launch `DCx.exe --readiness`. The decision, blockers and privacy-reviewed metric definitions are read-only and remain `NO_GO` while a critical or major blocker exists. There is no production activation action.

Local aggregate recording defaults off. The privacy group states the exact notice-version-1 exclusions. **Enable local aggregate recording** remains disabled until its separate consent is checked and resets that approval after use. **Stop recording and purge local aggregates** has a different unchecked approval and atomically disables recording and removes all retained aggregate buckets; an older client preserves the highest stored notice version so withdrawal cannot be blocked by version rollback protection. The status reports only the retained daily-bucket count.

These controls do not connect a product event source, export, transport, pilot enrollment or production activation. Do not add any of those paths without approved privacy/retention policy, explicit scope-specific consent, security review, human JAWS/NVDA/Narrator acceptance and a revised P14 gate.

## Already-installed local models

The current P06 gate expects a Windows-native Ollama service at the loopback origin `http://localhost:11434/`. DCx never acquires a dependency automatically and does not install or retag models. In the native app, open **Local models**, select a unique name ending in its matching SHA-256 digest, read the complete license text, check the default-off approval, and choose the eligible vision or embedding role. The per-user selection can be cleared independently for each role.

The same window reads a bounded native Windows hardware profile without WSL, a shell, child process or network request. Physical/available memory comes from the Windows kernel, CPU and graphics adapter/driver/total-memory evidence from read-only machine registry data, capacity from the application-data volume, and current NVIDIA free memory from the installed driver's exact `%WINDIR%\System32\nvml.dll`. DCx does not bundle NVML, load it through the search path or retain a PCI/device identifier. It applies its 70% current-free-NVIDIA-memory and 25% current-available-RAM limits to choose exact eligible vision and embedding identities. Missing live GPU evidence fails GPU selection closed; registry-only Intel/AMD observations remain visible but cannot establish GPU availability.

Production DCx vision, embedding and embedding-benchmark clients serialize their complete Ollama operations for the current Windows user. The coordinator uses an in-process semaphore plus exclusive access to the empty `%LOCALAPPDATA%\DCx\LocalModels\OllamaCoordination\.dcx-ollama-inference.lock` file. Do not delete or populate that file as routine cleanup. Queued acquisition is cancellable and bounded; a directory, reparse path or nonempty file at that location is a safety denial and must be inspected rather than overwritten. Under that lease, DCx recaptures live resource availability immediately before the exact model operation. Ordinary vision/embedding and benchmarks retain ownership through explicit unload and three consecutive observations that the exact digest is absent; unconfirmed cleanup fails closed. This coordinates only cooperating DCx processes, and memory can change after observation, so close unrelated Ollama and GPU clients during acceptance measurements.

An already-installed Whisper backend may be registered in the same window by selecting the local executable, model, runtime license and model license. Read both complete licenses and all four SHA-256 values, then check the default-off approval and save. Saving revalidates all four files; clearing removes only DCx state, not the selected files. DCx does not download or install Whisper from this surface, and registration alone does not establish publisher authenticity, license suitability or accuracy.

The audited dependency section can verify an existing local artifact against the immutable built-in catalog. Only an exactly matching Whisper runtime ZIP exposes the separate inert-staging acknowledgment. That acknowledgment starts unchecked, is revoked by any candidate or path edit, and permits a fresh authenticated copy into per-user DCx storage. Staging hashes every extracted file, records a source-path-free receipt and does not register, execute, install or select the runtime. A digest destination is immutable; an existing destination is never overwritten. Do not stage the real catalog candidate until the owner authorizes the documented P06 transfer and contained-license review.

The same section shows a keyboard-readable transfer preflight for the complete benchmark candidate set. It expands the Ollama manifest into exact managed blobs, deduplicates identical license objects, and binds every source, length, SHA-256 and role into plan SHA-256 `a0d997c6298fdf5f592241928c6fd9564c6de0c3e023c9eb69bd1df96406c286`. The current plan is nine transfer items and 1,281,058,770 network bytes. Its 3,098,988,452-byte minimum reserves an incoming copy, a retained verified copy and 512 MiB workspace. Observed free space is not part of the digest and is rechecked immediately before transfer. The preflight grants no authority by itself.

The inert core HTTP transport reloads the authenticated embedded catalog and re-derives that plan before accepting one explicitly approved exact item. Automatic redirects, proxies, credentials, preauthentication, cookies and decompression remain disabled. Startup is deny-all. **Observe current provider routes** is a separate explicit and cancellable action that sends bounded HEAD requests only, reads no response bodies and creates a non-persisted two-hour session. Schema-v2 transitions bind the exact item and exact query-free previous/target HTTPS paths, not a host or directory prefix; signed queries are neither displayed nor retained. The session's live policy also carries the exact reviewed plan digest. Both item transport and set coordination reject any different plan before provider payload contact or storage creation, including a valid direct-only or subset plan. The preflight shows the session expiry, canonical policy digest and every exact route. The later single-use transfer approval starts unchecked and binds the displayed plan and policy snapshots; missing/expired/substituted policy denies before a new payload request, a change during transfer rolls back, and the protected completed-set receipt records the historical policy digest. Every payload hop receives a fresh identity/no-store request without authorization, cookie or referrer headers. Final status, exact declared length, byte ceiling and SHA-256 remain mandatory. A rejected destination remains untouched; after admission, any ordinary cancellation, callback, timeout, denial or failure attempts truncate/rewind/flush. `dependency_transfer_cleanup_failed` means cleanup could not be proven and the destination must be discarded; the set coordinator removes its entire incomplete operation directory. The native window also exposes item/byte progress, cancellation, protected reverification and abandoned-partial recovery. Approval authorizes only displayed provider transfer plus inert storage, not staging or activation. Handler-backed contracts and native UIA fixtures pass, and one explicitly authorized clean-tree run invoked the actual product reviewer over current public routes with zero bodies, GETs, payload bytes, transfer approval or coordinator creation. The native UI action itself was not manually invoked, and no payload transfer occurred. Owner authorization remains required for this development run's real dependency operation.

The optional `scripts/audit-p06-provider-routes.ps1` command requires `-ExplicitReadOnlyNetworkAudit`. It derives the embedded plan and performs HEAD-only, body-free, no-credential route observation with the production transport restrictions and a bounded manual redirect loop. It strips signed queries from output and evidence and cannot modify the production policy. Do not run it implicitly during build, startup or preflight, and do not treat its result as transfer approval.

The separate `scripts/validate-p06-route-session.ps1 -ExplicitReadOnlyNetworkReview` command invokes the actual product reviewer from a required clean committed tree. It expects the current nine-item observation (12 HEADs, three redirects and eight exact lengths), rejects query-bearing evidence, and creates neither a coordinator nor transfer approval. It is an explicitly authorized external check, never part of build/startup/ordinary validation, and grants no payload authority.

The inert transfer-set coordinator supplies the confined local portion: it serializes transfer, verification and cleanup in process; rechecks free space before storage; transfers every item into a random private incoming directory; writes a source-path-free receipt; protects the receipt digest/length in current-user Windows Credential Manager; and atomically publishes only the complete plan-digest directory. Verification requires that protected fact and rehashes the exact inventory. Existing sets are never overwritten. Progress observers can still abort and clean up before publication; after atomic publication or completed verification, their best-effort notifications cannot rewrite durable success into a failed API result. Window-open and manual abandoned-incoming recovery run under normal DCx's exclusive data-root lease, recognize only strict names and refuse links. Synthetic native UIA proves the workflow with zero provider requests. Real transfer, contained-license and backend acceptance evidence remain absent.

`WhisperRuntimeStageVerifier` can later re-open only the catalog candidate's exact digest-bound stage and perform a read-only integrity review. The exact receipt digest, length and UTC are independently bound to the candidate in current-user Windows Credential Manager before publication. Verification holds the receipt and payloads under no-write/no-delete leases, rejects missing/malformed/conflicting protected anchors, malformed/unknown/duplicate receipt data, links and added/missing content, and rehashes each file. Credential Manager defends this boundary from filesystem-only replacement or rollback; it is not isolation from code already executing as the same Windows user. Verification is not registration, execution, installation, model selection, publisher authenticity, provenance approval or license approval; every eventual consumer must reauthenticate approved provenance and revalidate and lease the exact files it uses.

Nonempty Ollama, Whisper and FFmpeg approval-state files are separately bound to current-user Windows Credential Manager by exact byte length and SHA-256. Unanchored legacy state or a mismatched/replayed file is unavailable and must be reviewed and explicitly approved again; DCx never silently migrates it into trust. Clearing approval removes only DCx state and its protected fact, not source runtimes, models, executables or licenses. This protects against filesystem-only replacement, not arbitrary code already running as the same Windows user.

The files/media viewer can directly transcribe an admitted local WAV through that exact binding. MP3/MP4 transcription additionally requires a separately approved FFmpeg executable/license binding and uses a bounded, hash-reverified, leased temporary WAV that is deleted after Whisper finishes. It exposes cancellation and a read-only accessible transcript but does not persist or export transcript text. Frame extraction uses the same approved FFmpeg boundary and never selects an executable from `PATH`.

The local-model window reviews an already-installed FFmpeg executable plus its complete license and real Windows signature status before either media consumer can use it. Approval is per-user, explicit and revocable; no acquisition action exists. Do not bundle or approve the measured unsigned GPLv3 host build for distribution without owner and legal review.

The recorded development host has one approved vision fixture alias but no installed embedding model or real Whisper backend. Do not add either dependency without owner approval and recorded source, version, license, digest, resource and quality evidence. A malformed selection file fails closed; it must not be replaced with an environment-variable bypass. Run P06 with PowerShell 7 because its native evidence loads the .NET production assembly:

```powershell
pwsh.exe -NoProfile -File .\scripts\validate-p06.ps1
```

The **CARE recipients** window is a local-only owner-preparation surface. It requires exact pseudonym, email and digest review; uses separate default-unchecked approvals for save and revoke; stores the exact binding only in current-user Windows Credential Manager; and writes only minimized non-email metadata to its index. It never establishes PayPal verification or eligibility and exposes no payment, payout or network action. The P11 native validator uses a generated fixture address and deletes both the snapshot and protected credential in a `finally` cleanup path.

The global-service assembly contains schema-v13 registered CARE-period economics/weights/allocations/holds, contributor-period plus aggregate-liability payout reservation, failed-item remediation, verified-event dependency quarantine, minimized operator-decision/disposition audit, durable aggregate alert outbox and opaque blocked-case boundaries for local handler-backed tests. Registration and balanced accrual commit atomically; initial/remediation reservations recompute stored allocation integrity and require both exact contributor-period capacity and aggregate payable liability in one immediate same-database transaction. This is a local sandbox subledger, not authoritative production period-close or deployment authorization. Quarantine admits only an officially verified sale/correction with a missing durable binding. `DurableCareOperationsSource` defaults processing disabled and binds exact reviewed aggregate value, approval and the bounded batch to one same-database transaction. A process-boundary fixture exists only when `DCX_GLOBAL_SIMULATION=1`, `DCX_GLOBAL_CARE_OPERATIONS_API=1`, `DCX_GLOBAL_CARE_OPERATOR_TOKEN_SHA256` and the separate non-authorizing `DCX_GLOBAL_CARE_OPERATOR_PSEUDONYM_SHA256` are configured; replay mutation also requires `DCX_GLOBAL_CARE_PROCESSING=1`. It exposes only aggregate health/process and opaque blocked-case routes under `/api/care/operations`; approved dispositions leave events quarantined and make zero financial/provider calls. The pseudonym digest is sensitive/linkable and must never derive from the bearer token. An optional read-only monitor requires `DCX_GLOBAL_CARE_MONITOR=1`; its interval is bounded from 100 through 300000 ms and defaults to 30000. Its same-database outbox and at-least-once receipt survive restart, but no external notification, acknowledgment/escalation, approved closure/requeue policy, retention/pruning or distributed scheduler exists. The Windows CARE window still defaults unavailable; `DCX_CARE_OPERATIONS_FIXTURE=1` enables only generated UIA data. Do not wire this into normal desktop launches or add credentials/provider access until authoritative close/adjustment roles and policy, real identity, MFA/TLS, secret custody, centralized externally anchored audit, distributed fencing, closure/requeue and external notification/escalation policy, retention, production monitoring and credentialed sandbox acceptance are approved.

P10 qualified-usage evaluation is local simulation code, not a public endpoint. Its authoritative caller must supply the exact installation pseudonym, entitlement ID, actor, active status, 32–128-byte installation key and UTC time. HMAC verification precedes one immediate SQLite transaction spanning replay, approved-resource and daily-count reads through the exact decision insert. This closes same-database replay and slot-100 races; it does not provision or rotate production keys, query an authoritative subscription system, attest client execution, authorize contribution payments or permit deployment.

## Ubuntu service

No real-server deployment is authorized. The bounded command below uses the preconfigured `ubuntu-server` SSH alias only for fixed read-only, nonprivileged capability checks; it must not be expanded into file transfer, build, container or service actions without approval.

```powershell
pwsh.exe -NoProfile -File .\scripts\audit-ubuntu-server.ps1
```

The recorded audit found Ubuntu 24.04, .NET/ASP.NET 10, Docker/Compose and Bubblewrap, but denied Docker daemon access, found no writable cgroup delegation and failed the exact Bubblewrap network-isolation smoke. P09 still needs explicit authority for any access change or Compose deployment. P10 still needs an approved remediation that preserves network denial and measured resource isolation.

The P09 client serializes every operation on one trust-root replica with a canonical-root in-process gate and persistent empty no-share lock file. This protects cooperating client instances/processes from checkpoint, policy, anchor-reconciliation and activation races. A queued operation can be cancelled; 30-second contention returns a stable retry message. Do not delete the lock file as routine cleanup. It is not a substitute for the dedicated service identity, ACL/mount policy, hardware-grade monotonic trust or container isolation still required for production.
