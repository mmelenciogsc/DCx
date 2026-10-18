# ADR 0091: Compose protected owned Ollama approval with bounded exact-model residency

- Status: Accepted for local vision production composition; malicious same-user isolation remains blocked
- Date: 2026-10-19

## Context

ADR 0090 proved that DCx can review and launch an independently owned Ollama process without changing the user's existing service. It deliberately stopped before product composition. Real RTX 4060 measurements then exposed two requirements that a production boundary must satisfy together: explicit durable consent for the exact signed runtime, and process/model reuse. Destroying the process and unloading the 4B vision model after every answer produced cold latencies between 73 and 241 seconds even though CUDA was active and all 37 layers were offloaded.

The runtime's `/api/ps` response also canonicalizes the approved digest-qualified request name to `sha256-<digest>:latest`. Current free-GPU admission cannot be reapplied unchanged after that exact model is resident because its own allocation correctly reduces reported free VRAM.

## Decision

DCx persists one schema-bounded runtime configuration containing the exact ADR 0090 runtime binding, canonical non-reparse local model root and approval time. The file lives separately from model/Whisper/FFmpeg selections, is atomically replaced under the existing path guard and is authenticated by a separate per-user Windows Credential Manager anchor. Rollback, missing anchor, linked path, malformed state, unsigned runtime, changed license reference, stale review or non-explicit approval fails closed.

The accessible local-model window exposes runtime executable, model folder and dependency-profile controls; exact file count/bytes/tree digest, Authenticode status, signer/certificate digest and official license reference; and separate Review, initially unchecked Approve, Save and Clear actions with unique access keys. Review starts nothing. Save re-reviews the exact identity before recording approval. Clear revokes only authority and deletes no runtime or model content.

Normal local-frame and shared-viewport video vision requests use one application-owned host. Before every operation it re-reads the protected approval. A changed, missing or invalid approval stops the prior process. The host serializes operations, starts only the exact signed tree on a random PID-proven loopback port and retains at most one requested digest-bound model for five minutes. A second model request restarts the owned process rather than sharing residency.

Warm admission accepts only the exact requested inventory name or Ollama's deterministic `sha256-<approved digest>:latest` process alias, and always requires the approved digest. Any other name/digest, duplicate or unrelated model fails and tears down the whole owned process. Model inventory, capability and complete-license digest are revalidated before and after every answer. Fresh resource admission runs before a cold load; it is not charged again when three observations prove that the exact target is already resident. Post-response process inventory must still contain exactly that target.

Failures, cancellation, unsafe response state, configuration changes, explicit revocation, benchmark isolation and application exit kill the owned process tree through the existing job boundary. The external desktop Ollama service is never stopped or reconfigured. Embedding benchmarks stop an interactive host before creating their separately measured cold runtime. No pull, copy, installation, service change or model transfer is authorized by this decision.

## Validation

Forty focused runtime/client/store tests pass. They cover protected approval and rollback, path/link denial, exact-tree launch, process reuse, revocation and failure teardown, canonical-alias admission, unrelated-resident denial, failure cleanup, cold-only resource admission and strict legacy shared-client behavior.

The real-host gate reviewed Ollama 0.40.0 as the signed 63-file, 769,163,769-byte CUDA-v13 tree with SHA-256 `1abc86c70ee5a6337e8054b1bf0bdc13d61a13e4461b30995be353b0dd3d0999`. Its accepted run measured 240,837 ms cold and 1,155 ms warm in one PID. The complete P06 run independently measured 220,013 ms cold and 1,251 ms warm, preserved the existing service, then cleared approval and proved owned process/listener cleanup. P06 passed 230/230; the full suite passed 586/586.

Native UI Automation proved default-off runtime approval, exact signer/tree/license review, protected save, owned inventory, local MP4 description and a same-app web-video follow-up. It measured 73,570 ms cold and 2,223 ms warm, retained playback pause/resume semantics, killed the owned runtime on DCx exit and preserved the pre-existing service.

## Consequences and residual risk

- Follow-up descriptions avoid repeated server/model initialization while the exact approved model remains resident.
- Five-minute residency consumes GPU memory after an answer; Ollama may unload the model earlier under pressure. The next empty observation receives a fresh resource-admission check.
- Runtime/model approval is per user and protected against ordinary file rollback, not against an administrator or malicious code running as that same user.
- Ollama loopback has no client authentication. Same-user malware can discover the port and race or submit requests. Exact inventory checks can detect many state changes but do not provide request attribution. AppContainer/ACL or an authenticated inference broker remains unresolved.
- The model directory remains externally mutable. Exact inventory/license/digest checks fail closed but cannot remove every same-user check/use race.
- The experimental CUDA-v13 override is accepted only for this reviewed tree/version/hardware and must be revalidated after change.
- Human JAWS/NVDA/Narrator acceptance, real Whisper and embedding backends, Intel/AMD compute acceptance and redistribution/legal approval remain blocked.

## Alternatives rejected

- Continue unloading after every answer: secure cleanup passed, but measured latency made interactive follow-up unusable.
- Skip process inventory on warm requests: fast, but unable to detect an unrelated or substituted resident model.
- Accept any resident name with the same digest: unnecessarily broad; only the exact request name and deterministic digest-derived Ollama alias are admitted.
- Reapply cold free-VRAM admission to a resident target: counts DCx's own exact allocation as unavailable and incorrectly rejects every follow-up.
- Reuse the user's default service: preserves the ambiguity and interference risks addressed by ADR 0090.
