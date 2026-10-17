# P06 — Local models and resource inventory

- Status: BLOCKED (real vision passes; transcription/embedding contracts pass fixtures; actual backends remain absent)
- Implementation commits: `463bfb6`, `b4aa4c9`, `a160435`, `21001d2`, `a41702b`
- Documentation commit: recorded by the commit containing this file

No model or runtime was downloaded or installed. The real benchmark uses only software and models already present on this Windows machine.

## Measured inventory

- Windows GPU: NVIDIA GeForce RTX 4060 Laptop GPU; 8,188 MiB VRAM; driver 610.78.
- Ollama: Windows-native 0.40.0 at `D:\Programs\Ollama\ollama.exe`.
- Installed vision candidates: Qwen3-VL 2B (about 1.9 GB), 4B (about 3.3 GB) and 8B (about 6.2 GB).
- Mutable `qwen3-vl:4b-instruct` and `qwen3-vl:8b-instruct` each appear with multiple digests and are therefore ineligible for deterministic selection. The prior first-row digest claim did not bind the subsequently invoked mutable name and is superseded by this gate.
- Selected candidate: unique digest-qualified alias `llamacpp:ef33995bb2ac37689e49c1a15cd627354d05a3c21d1f4bdcd6a5c83825349c1f`, SHA-256 model digest `ef33995bb2ac37689e49c1a15cd627354d05a3c21d1f4bdcd6a5c83825349c1f`, 4.4B parameters, Q4_K_M, and advertised completion/vision/tools capabilities.
- Inspected model license begins `Apache License`; license-text SHA-256 is `d0fa3710cf6cf8e18009051c28988e4aa8114b0d4cd6e3f102bb9ccba50d7cc7`.
- FFmpeg: Windows-native 2025-03-24 git build with CUDA/NVDEC/NVENC support.
- Whisper: no installed `whisper-cli` detected.
- Embeddings: all installed Ollama models were inspected through loopback `/api/show`; zero advertise `embedding`.

## Implemented

- Loopback-only Ollama adapter with bounded and schema-validated JSON responses for model inventory, `/api/show`, nonstreaming vision and `/api/embed`.
- The production Ollama transport owns a no-redirect, no-cookie, no-proxy HTTP handler, accepts only a root loopback origin, and rejects any response whose scheme/host/port differs from that origin.
- Inventory requires a bounded regular name, positive size, 64-hex digest, details object and at most 32 bounded capability values. Inference requires one unique digest-qualified alias with the expected digest, capability and inspected license-text SHA-256 both before and after execution.
- A keyboard- and UIA-accessible per-user manager reads only the loopback Ollama inventory. It displays complete installed-model license text, exact model and license digests, capabilities and size; it exposes no download, install, retag or upload action.
- Vision and embedding approval are independent and default unchecked. Save re-reads the inventory and license, requires the reviewed unique digest-qualified identity, and resets the approval checkbox after success.
- The bounded schema-v1 selection store records only exact name, model digest, license digest and approval UTC. It rejects future/unknown/malformed/mutable state, roots, reparse points and unsafe state-path replacement; same-directory write-through temporary files are atomically moved into place.
- Media frame description no longer consumes environment-selected model names. It reads the per-user approved vision binding, remains paused when no binding exists, and passes all three identity fields to the production adapter.
- Resource-aware vision/embedding selection excludes mutable and duplicate names. A changed binding during inference rejects the result.
- Embedding requests allow at most 32 bounded texts, disable truncation, explicitly unload, require request/response model and count agreement, and reject empty, inconsistent, oversized or nonfinite vectors.
- Image size/prompt bounds, PNG/JPEG/WebP signature admission, deterministic temperature/output limit, cancellation and explicit unload. Vision responses must identify the requested model, be complete, nonempty and bounded.
- Hardware-aware vision selection uses at most 70% of measured VRAM. Embedding selection prefers the smallest installed model within 25% of measured RAM; low-resource no-fit tests fail closed.
- Fallback order is validated local GPU, bounded local CPU, then an explicitly approved reachable remote provider only for nonprivate input. Private input with no local backend becomes unavailable.
- Hash-pinned `whisper-cli.exe` and `.bin`/`.gguf` model boundary. Verified executable/model files remain under read-only, no-write/no-delete leases for the entire child execution, closing the hash-check/use race. It accepts bounded signature-checked WAV only, uses argument arrays and a scrubbed environment, drains but never retains child diagnostics, enforces timeout/tree kill, bounds transcript output, records working set and removes its operation directory.
- A native fake Whisper CLI validates process/argument/output/cancellation behavior. It does not count as speech-recognition accuracy evidence.

## Reproduction

```bash
cd /mnt/d/DCx
powershell.exe -NoProfile -ExecutionPolicy Bypass -File D:\DCx\scripts\validate-p06.ps1
powershell.exe -NoProfile -ExecutionPolicy Bypass -File D:\DCx\scripts\ci.ps1
```

## Measured result

- Locked Release build: 0 warnings, 0 errors; 2.76 seconds in the recorded P06 run.
- P06 exact-namespace local-model tests: passed 32, failed 0, skipped 0; about 1 second, with counters parsed from retained TRX.
- Current full suite after P09 protected trust-policy anchoring: passed 211, failed 0, skipped 0; about 9 seconds.
- Vision response: `A blue square and a red circle are displayed above the text "DCX 42".`
- Production-adapter prompt tokens: 1,100; output tokens: 20; total duration: 16,426,065,000 ns.
- Separately measured Ollama duration: 16,626,554,500 ns; wall duration: 18,698 ms.
- Ollama-reported loaded model and VRAM allocation: 3,527,545,978 bytes each.
- Exact digest and license binding before/after inference, malformed inventory/digest rejection, duplicate/mutable selection denial, changed-binding denial and changed-license denial passed.
- Explicit model unload verification by loaded-process digest: passed.
- Hash-pinned fake Whisper happy path, model-tamper denial, invalid-WAV denial and timeout/tree-kill passed.
- Whisper pinned-file write replacement was denied while the child fixture ran; the leases released after cancellation.
- Ollama inspection/embed request shape, capability/license parsing, two-vector response and inconsistent-dimension denial passed.
- Ollama redirect/cookie/proxy denial, exact response-origin enforcement, image-signature admission and mismatched/incomplete response denial passed.
- The P07 native WebView2/UIA media test selected the installed model through the accessible manager, read the complete license through `ValuePattern`, proved approval initially and subsequently unchecked, persisted the binding, found no download action, and passed local MP4 pause/describe/resume with the real loopback VLM.
- Hardware/TRX evidence: `artifacts/test-results/p06-731b153275134aebafbc779661eed96a/`; P07 UIA fixtures: `artifacts/test-results/p07-a86adb37eebb4f198b2e6d3b32184973/` (generated and intentionally not committed).

## Changed files in the latest milestone

- Persisted approval boundary: `src/DCx.Core/LocalModels/LocalModelSelectionStore.cs`, `src/DCx.Desktop/LocalModelsWindow.xaml`, `src/DCx.Desktop/LocalModelsWindow.xaml.cs`.
- Digest/license identity and resource selection: `src/DCx.Core/LocalModels/LocalModelDescriptor.cs`, `src/DCx.Core/LocalModels/LocalModelSelector.cs`, `src/DCx.Core/LocalModels/OllamaLocalClient.cs`.
- Production consumer and validation seam: `src/DCx.Desktop/Media/MediaFrameDescriptionService.cs`, `src/DCx.Desktop/ContentViewerWindow.xaml.cs`, `src/DCx.Desktop/MainWindow.xaml`, `src/DCx.Desktop/MainWindow.xaml.cs`, `scripts/validate-p07.ps1`.
- Identity, mutation, malformed-inventory, state-store and selection denial coverage: `tests/DCx.Core.Tests/LocalModels/OllamaLocalClientTests.cs`, `tests/DCx.Core.Tests/LocalModels/LocalModelSelectorTests.cs`, `tests/DCx.Core.Tests/LocalModels/LocalModelSelectionStoreTests.cs`.
- Exact-counter installed-hardware evidence: `scripts/validate-p06.ps1`.
- Decision, development and human-AT records: `docs/architecture/adr/0023-explicit-persisted-local-model-approval.md`, `docs/deployment/DEVELOPMENT.md`, `docs/accessibility/P06_LOCAL_MODELS_CHECKLIST.md`.

## Blockers and unblock condition

- The native fake CLI proves the broker contract, not transcription accuracy, license suitability or real resource use. No approved Whisper executable/model exists on the machine.
- The embedding mock proves the bounded loopback contract, not retrieval quality, actual dimensions, license suitability or real memory/latency. No installed model advertises embeddings.
- The digest-qualified alias and before/after runtime checks bind the runtime-reported bytes and reviewed license text used for the measured call; they do not establish publisher authenticity, signed provenance, legal license approval or a production model install/update/revocation policy. The accessible manager selects only already-installed local models and deliberately performs no installation.
- Automated UIA passed, but the dedicated JAWS/NVDA/Narrator checklist has not been run and cannot be inferred from automation.
- Install/download of whisper.cpp and a suitable embedding model, with source, version, license and SHA-256 recorded, requires owner approval.
- After approval, run fixed speech accuracy/word-error fixtures and retrieval recall/ranking fixtures, measure CPU/GPU/RAM/VRAM, latency, cancellation and unload under normal and low-resource emulation. P06 remains BLOCKED until those real gates pass.
- No cloud Ollama endpoint, API key or network model call was used.
