# ADR 0090: Bind an independently owned Ollama runtime before production composition

- Status: Accepted as a validated isolation foundation; accessible approval and production composition remain blocked
- Date: 2026-10-19

## Context

ADR 0089 detects activity on the user's shared Ollama service without interrupting it. That is deliberately non-destructive, but it cannot exclude an external client that begins after preflight or attribute a concurrent request for the same model. P06 therefore needs a runtime that DCx starts, verifies, observes and stops independently before local inference can claim stronger process and resource attribution.

The installed Ollama 0.40.0 distribution contains mutually exclusive CUDA, ROCm and Vulkan payloads. Hashing or leasing every unused backend would add about 3 GB of work without making the NVIDIA execution path more deterministic. Ollama officially documents `OLLAMA_LLM_LIBRARY` as an experimental override for an available library, and the installed runtime reports `cuda_v13` as its NVIDIA library path.

## Decision

DCx adds an explicit runtime review and owned-process boundary. Review binds the canonical executable, Windows Authenticode evidence, a named backend profile, every admitted relative path, byte length, SHA-256 and a domain-separated tree digest. The NVIDIA profile admits root files, direct `lib` and `lib/ollama` files, and the complete `lib/ollama/cuda_v13` subtree. It requires both `llama-server.exe` and `cuda_v13/ggml-cuda.dll`. CUDA 12, ROCm and Vulkan subtrees are outside that profile; launch forces `cuda_v13` and disables Vulkan.

Launch requires a distinct explicit-approval argument. It re-enumerates, rehashes and holds all 63 admitted files under no-write/no-delete read leases. It uses the direct non-reparse model root, a random IPv4 loopback port, a scrubbed environment, a private disposable `USERPROFILE`/temporary directory, `OLLAMA_NO_CLOUD=1`, `OLLAMA_NOPRUNE=1`, zero keep-alive and one-model/one-parallel/one-queue limits. It invokes no pull, create, copy, delete or unload API.

DCx accepts readiness only from a bounded, exact-schema `/api/version` response after the Windows TCP owner table proves that the listener belongs to the new child PID. A Windows job object has kill-on-close enabled before readiness, child diagnostics are drained but never retained, and cancellation, timeout, denial or disposal kills the process tree, closes leases and removes the private profile. The existing desktop-owned Ollama process and its port are neither stopped nor reconfigured.

This milestone does not silently replace the production client. The owned runtime binding is not persisted, no accessible default-off review/approval surface selects it, and media/benchmark composition still uses the shared adapter and ADR 0089 fence. A later milestone must protect persisted runtime consent, expose exact signer/profile/tree/license evidence accessibly, and pass real inference/resource/unload regressions through the owned instance before changing production composition.

## Validation

At exact source `69aabc0`, ten contracts pass. They cover complete-tree and CUDA-v13 profile identity, link and signer denial, malformed binding denial, environment scrubbing, explicit approval, PID ownership, runtime mutation/addition denial, early child exit, shutdown, model-file preservation and kill-on-job-close. All ten passed in ten consecutive focused runs.

The real-host gate reviewed signed Ollama 0.40.0 into a 63-file, 769,163,769-byte CUDA-v13 profile with tree SHA-256 `1abc86c70ee5a6337e8054b1bf0bdc13d61a13e4461b30995be353b0dd3d0999`. It started an independent loopback listener, listed eight installed models, performed no inference or transfer, stopped the owned process/listener, removed its private profile and proved the existing service survived. P06 passed 220/220 and the expanded suite passed 576/576.

## Consequences and residual risk

- An accidental ordinary client using the default service no longer shares the owned process or port.
- A port-reuse race must still pass exact child-PID listener ownership.
- Runtime update or admitted-file mutation invalidates the tree and requires a new review.
- The installed model store is shared read-only by convention; exact model identity checks remain mandatory and arbitrary same-user code can still mutate it.
- Loopback Ollama has no per-client authentication. Malicious same-user code can discover and call the random port after readiness, so this is not a security boundary against code already running as that user.
- Directory enumeration and process creation still have a narrow same-user check/use race despite file leases. Production ACL/AppContainer or equivalent process isolation remains unresolved.
- The official license URL and contained third-party notices were identified, but this decision grants no legal approval, redistribution right, download, installation or model-transfer authority.
- The experimental library override passed this exact installed version and hardware. Runtime/profile changes require re-review and revalidation.

## Alternatives rejected

- Stop or reuse the desktop-owned service: DCx has no authority to disrupt the user's other clients.
- Treat the cooperative lock as exclusive: unrelated clients do not honor it.
- Trust a free random port without owner-PID proof: another process could win the bind race.
- Inherit the user's environment or profile: that would expose ambient secrets/state and allow unintended cloud or configuration behavior.
- Copy the runtime into DCx storage automatically: copying or installing a real runtime requires separate user and licensing authority.
- Claim production migration from a validation-only launcher: the accessible consent, persistence and real owned-inference composition gates have not passed.
