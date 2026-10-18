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

## Already-installed local models

The current P06 gate expects a Windows-native Ollama service at the loopback origin `http://localhost:11434/`. DCx never acquires a dependency automatically and does not install or retag models. In the native app, open **Local models**, select a unique name ending in its matching SHA-256 digest, read the complete license text, check the default-off approval, and choose the eligible vision or embedding role. The per-user selection can be cleared independently for each role.

The same window reads a bounded native Windows hardware profile without WSL, a shell, child process or network request. Physical memory comes from the Windows kernel, CPU and graphics adapter/driver/memory evidence from read-only machine registry data, and capacity from the application-data volume. Graphics values are explicitly registry-reported and are only a prefilter. DCx applies its 70% graphics-memory and 25% physical-RAM limits to choose the exact eligible vision and embedding identities, then recaptures hardware and inventory immediately before approval is saved. Missing or changed resource evidence fails closed; real benchmark quality, GPU-use, peak-resource, latency and unload gates are still required.

An already-installed Whisper backend may be registered in the same window by selecting the local executable, model, runtime license and model license. Read both complete licenses and all four SHA-256 values, then check the default-off approval and save. Saving revalidates all four files; clearing removes only DCx state, not the selected files. DCx does not download or install Whisper from this surface, and registration alone does not establish publisher authenticity, license suitability or accuracy.

The audited dependency section can verify an existing local artifact against the immutable built-in catalog. Only an exactly matching Whisper runtime ZIP exposes the separate inert-staging acknowledgment. That acknowledgment starts unchecked, is revoked by any candidate or path edit, and permits a fresh authenticated copy into per-user DCx storage. Staging hashes every extracted file, records a source-path-free receipt and does not register, execute, install or select the runtime. A digest destination is immutable; an existing destination is never overwritten. Do not stage the real catalog candidate until the owner authorizes the documented P06 transfer and contained-license review.

The same section shows a keyboard-readable transfer preflight for the complete benchmark candidate set. It expands the Ollama manifest into exact managed blobs, deduplicates identical license objects, and binds every source, length, SHA-256 and role into plan SHA-256 `a0d997c6298fdf5f592241928c6fd9564c6de0c3e023c9eb69bd1df96406c286`. The current plan is nine transfer items and 1,281,058,770 network bytes. Its 3,098,988,452-byte minimum reserves an incoming copy, a retained verified copy and 512 MiB workspace. Observed free space is not part of the digest and is rechecked immediately before transfer. The preflight grants no authority by itself.

The inert core HTTP transport reloads the authenticated embedded catalog and re-derives that plan before accepting one explicitly approved exact item. Automatic redirects, proxies, credentials, preauthentication, cookies and decompression remain disabled. A manual redirect loop exists behind a production deny-all policy; it can follow at most three nonrepeating HTTPS hops only when a compiled rule binds the exact item, source origin and non-root target origin/path prefix. Every hop receives a fresh identity/no-store request without authorization, cookie or referrer headers. Final status, exact declared length, byte ceiling and SHA-256 remain mandatory, and cancellation, timeout, denial or failure truncates partial output. The native window exposes a separate unchecked exact-plan approval, **Begin approved transfer**, item/byte progress, cancellation, protected reverification and abandoned-partial recovery. The approval is single-use and authorizes only displayed provider transfer plus inert storage, not staging or activation. No real provider rule is configured. Do not approve a real transfer until current official direct-origin/redirect behavior is verified, the exact rule is reviewed and the owner authorizes the operation.

The inert transfer-set coordinator supplies the confined local portion: it serializes transfer, verification and cleanup in process; rechecks free space before storage; transfers every item into a random private incoming directory; writes a source-path-free receipt; protects the receipt digest/length in current-user Windows Credential Manager; and atomically publishes only the complete plan-digest directory. Verification requires that protected fact and rehashes the exact inventory. Existing sets are never overwritten. Window-open and manual abandoned-incoming recovery run under normal DCx's exclusive data-root lease, recognize only strict names and refuse links. Synthetic native UIA proves the workflow with zero provider requests. Real transfer, contained-license and backend acceptance evidence remain absent.

`WhisperRuntimeStageVerifier` can later re-open only the catalog candidate's exact digest-bound stage and perform a read-only integrity review. The exact receipt digest, length and UTC are independently bound to the candidate in current-user Windows Credential Manager before publication. Verification holds the receipt and payloads under no-write/no-delete leases, rejects missing/malformed/conflicting protected anchors, malformed/unknown/duplicate receipt data, links and added/missing content, and rehashes each file. Credential Manager defends this boundary from filesystem-only replacement or rollback; it is not isolation from code already executing as the same Windows user. Verification is not registration, execution, installation, model selection, publisher authenticity, provenance approval or license approval; every eventual consumer must reauthenticate approved provenance and revalidate and lease the exact files it uses.

Nonempty Ollama, Whisper and FFmpeg approval-state files are separately bound to current-user Windows Credential Manager by exact byte length and SHA-256. Unanchored legacy state or a mismatched/replayed file is unavailable and must be reviewed and explicitly approved again; DCx never silently migrates it into trust. Clearing approval removes only DCx state and its protected fact, not source runtimes, models, executables or licenses. This protects against filesystem-only replacement, not arbitrary code already running as the same Windows user.

The files/media viewer can directly transcribe an admitted local WAV through that exact binding. MP3/MP4 transcription additionally requires a separately approved FFmpeg executable/license binding and uses a bounded, hash-reverified, leased temporary WAV that is deleted after Whisper finishes. It exposes cancellation and a read-only accessible transcript but does not persist or export transcript text. Frame extraction uses the same approved FFmpeg boundary and never selects an executable from `PATH`.

The local-model window reviews an already-installed FFmpeg executable plus its complete license and real Windows signature status before either media consumer can use it. Approval is per-user, explicit and revocable; no acquisition action exists. Do not bundle or approve the measured unsigned GPLv3 host build for distribution without owner and legal review.

The recorded development host has one approved vision fixture alias but no installed embedding model or real Whisper backend. Do not add either dependency without owner approval and recorded source, version, license, digest, resource and quality evidence. A malformed selection file fails closed; it must not be replaced with an environment-variable bypass. Run P06 with PowerShell 7 because its native evidence loads the .NET production assembly:

```powershell
pwsh.exe -NoProfile -File .\scripts\validate-p06.ps1
```

The **CARE recipients** window is a local-only owner-preparation surface. It requires exact pseudonym, email and digest review; uses separate default-unchecked approvals for save and revoke; stores the exact binding only in current-user Windows Credential Manager; and writes only minimized non-email metadata to its index. It never establishes PayPal verification or eligibility and exposes no payment, payout or network action. The P11 native validator uses a generated fixture address and deletes both the snapshot and protected credential in a `finally` cleanup path.

The global-service assembly contains schema-v11 failed-item remediation, verified-event dependency quarantine, minimized operator-decision/disposition audit, durable aggregate alert outbox and opaque blocked-case boundaries for local handler-backed tests. Quarantine admits only an officially verified sale/correction with a missing durable binding. `DurableCareOperationsSource` defaults processing disabled and binds exact reviewed aggregate value, approval and the bounded batch to one same-database transaction. A process-boundary fixture exists only when `DCX_GLOBAL_SIMULATION=1`, `DCX_GLOBAL_CARE_OPERATIONS_API=1`, `DCX_GLOBAL_CARE_OPERATOR_TOKEN_SHA256` and the separate non-authorizing `DCX_GLOBAL_CARE_OPERATOR_PSEUDONYM_SHA256` are configured; replay mutation also requires `DCX_GLOBAL_CARE_PROCESSING=1`. It exposes aggregate health/process plus opaque blocked-case list/disposition routes under `/api/care/operations`, uses the simulation origin and rate limiter, and is not production deployment guidance. Blocked-case dispositions accept only explicitly approved `investigation_requested`/`retained_blocked`, leave events quarantined and make zero financial/provider calls. The pseudonym digest is sensitive/linkable and must never be derived from the bearer token. An optional read-only monitor requires `DCX_GLOBAL_CARE_MONITOR=1`; `DCX_GLOBAL_CARE_MONITOR_INTERVAL_MS` is bounded from 100 through 300000 and defaults to 30000. It emits only fixed aggregate log transitions and never processes work. Its same-database outbox and at-least-once delivery receipt survive restart; no external notification, human acknowledgment/escalation, approved closure/requeue policy, retention/pruning or distributed-service scheduler exists. The Windows CARE operations window still defaults unavailable; `DCX_CARE_OPERATIONS_FIXTURE=1` enables only fixed generated UIA data. Do not wire the loopback client into normal desktop launches or add credentials/provider access until real identity, MFA/TLS, secret custody, centralized externally anchored audit, distributed fencing, closure/requeue and external notification/escalation policy, retention, production monitoring and credentialed sandbox acceptance are approved.

## Ubuntu service

No real-server deployment is authorized. The bounded command below uses the preconfigured `ubuntu-server` SSH alias only for fixed read-only, nonprivileged capability checks; it must not be expanded into file transfer, build, container or service actions without approval.

```powershell
pwsh.exe -NoProfile -File .\scripts\audit-ubuntu-server.ps1
```

The recorded audit found Ubuntu 24.04, .NET/ASP.NET 10, Docker/Compose and Bubblewrap, but denied Docker daemon access, found no writable cgroup delegation and failed the exact Bubblewrap network-isolation smoke. P09 still needs explicit authority for any access change or Compose deployment. P10 still needs an approved remediation that preserves network denial and measured resource isolation.
