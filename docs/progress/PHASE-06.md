# P06 — Local models and resource inventory

- Status: BLOCKED (real vision passes; transcription/embedding contracts pass fixtures; real Whisper/embedding backends remain absent)
- Implementation commits: `463bfb6`, `b4aa4c9`, `a160435`, `21001d2`, `a41702b`, `5cdf5ec`, `ed6f001`, `2f71398`, `acc8e62`
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
- The accessible manager can now register an already-installed Whisper executable, model and two license files. It accepts only four distinct regular files on local drives, rejects reparse ancestors, bounds executable/model/license sizes, displays both complete UTF-8 licenses and all four exact byte digests, starts approval unchecked, invalidates review on any path edit, and rehashes all four files immediately before atomic persistence.
- The Whisper registration actions expose stable UIA identifiers and distinct keyboard access keys. The manager has no Whisper download/install action. Clearing removes only the per-user binding and preserves every selected source file. Registration explicitly does not claim publisher authenticity, legal license approval or transcription accuracy.
- Adapter creation revalidates both approved license-file digests. Each transcription revalidates bounded executable/model digests and holds the executable, model and WAV under no-write/no-delete read leases through child completion. UNC/device paths and reparse ancestors fail closed.
- The files/media viewer pauses admitted WAV, MP3 and MP4 media and consumes only persisted approved bindings. WAV goes directly to Whisper. MP3/MP4 require the FFmpeg binding, announce bounded audio extraction separately, then pass a leased temporary WAV to Whisper. Cancellation remains available, no canceled/cleared-binding stale text is retained, the completed read-only transcript receives focus, and media stays paused.
- A separate accessible FFmpeg manager reviews one existing local executable and license under read leases, exposes exact byte digests, full bounded UTF-8 license and real Windows Authenticode status, and requires default-off approval plus save-time revalidation. It cannot download, install, bundle, delete or upload FFmpeg.
- The approved FFmpeg processor revalidates the license at construction and executable digest per run; holds the executable and signature-checked MP3/MP4 under read-only/no-write/no-delete leases; uses an absolute executable, typed arguments and scrubbed environment; bounds input/output/time; tree-kills on cancel/timeout; accepts only validated WAV/PNG output; and deletes private random operation storage when the leased artifact is disposed.
- Local MP4 frame description now requires and consumes this exact approved FFmpeg binding. The legacy `PATH` search and independent process implementation were removed.
- A native fake Whisper CLI validates process/argument/output/cancellation behavior. It does not count as speech-recognition accuracy evidence.

## Reproduction

```bash
cd /mnt/d/DCx
pwsh.exe -NoProfile -ExecutionPolicy Bypass -File D:\DCx\scripts\validate-p06.ps1
pwsh.exe -NoProfile -ExecutionPolicy Bypass -File D:\DCx\scripts\ci.ps1
```

## Measured result

- Locked Release build: 0 warnings, 0 errors; 1.75 seconds in the recorded P06 run.
- P06 exact-namespace local-model tests: passed 60, failed 0, skipped 0; about 2 seconds, with counters parsed from retained TRX.
- Current full suite: passed 289, failed 0, skipped 0; about 12 seconds.
- Vision response: `A blue square and a red circle are displayed above the text "DCX 42".`
- Production-adapter prompt tokens: 1,100; output tokens: 20; total duration: 15,823,987,500 ns.
- Separately measured Ollama duration: 15,904,978,200 ns; wall duration: 17,930 ms.
- Ollama-reported loaded model and VRAM allocation: 3,527,545,978 bytes each.
- Exact digest and license binding before/after inference, malformed inventory/digest rejection, duplicate/mutable selection denial, changed-binding denial and changed-license denial passed.
- Explicit model unload verification by loaded-process digest: passed.
- Hash-pinned fake Whisper happy path, model-tamper denial, invalid-WAV denial and timeout/tree-kill passed.
- Whisper pinned-file write replacement was denied while the child fixture ran; the leases released after cancellation.
- Native UIA proved the Whisper approval begins unchecked, saving without approval is denied, both complete licenses and all four digests are exposed, action access keys are unique, and no download/install action exists. The UI-persisted binding produced `DCx transcription fixture 42.` through both the production adapter and accessible media viewer. User cancellation killed the fixture and retained no partial text; completion focused the transcript; clearing denied the consumer and removed stale text while retaining all four source files.
- FFmpeg native UIA proved exact executable/license digest and full-license review, real `NotSigned` status, default-off/save-before-approval denial, unique keyboard access keys, persisted identity, approval reset and clear-without-delete. The real installed candidate measured 148,583,424 bytes, executable SHA-256 `943c5c0623ded76eb02e4b593a7ac1bac043fbbcf6d1d34d673fb8c0369fab52`, GPLv3 license SHA-256 `8ceb4b9ee5adedde47b31e975c1d90c73ad27b6b165a1dcd80c7c545eb65b903`, Windows `NotSigned`, and `approved=false`.
- Seven processor tests passed leased WAV/PNG success and cleanup, input/output signature denial, executable and license tamper denial, cancellation/timeout tree kill, lease release and active input write denial. Native UIA composed the approved fixture FFmpeg and Whisper bindings for MP3 transcription, focused the expected transcript, then proved immediate denial after clearing FFmpeg.
- Ollama inspection/embed request shape, capability/license parsing, two-vector response and inconsistent-dimension denial passed.
- Ollama redirect/cookie/proxy denial, exact response-origin enforcement, image-signature admission and mismatched/incomplete response denial passed.
- The P07 native WebView2/UIA media test selected the installed model through the accessible manager, read the complete license through `ValuePattern`, proved approval initially and subsequently unchecked, persisted the binding, found no download action, and passed local MP4 pause/describe/resume with the real loopback VLM.
- Hardware/TRX/native-registration evidence: `artifacts/test-results/p06-ec2254a02625414ea7fe8eb09ba23453/`; P07 UIA fixtures: `artifacts/test-results/p07-15ebf4df5ff14865b6e6944ce062fedd/` (generated and intentionally not committed).

## Changed files in the latest milestone

- Persisted approval boundary: `src/DCx.Core/LocalModels/LocalModelSelectionStore.cs`, `src/DCx.Desktop/LocalModelsWindow.xaml`, `src/DCx.Desktop/LocalModelsWindow.xaml.cs`.
- Digest/license identity and resource selection: `src/DCx.Core/LocalModels/LocalModelDescriptor.cs`, `src/DCx.Core/LocalModels/LocalModelSelector.cs`, `src/DCx.Core/LocalModels/OllamaLocalClient.cs`.
- Production consumer and validation seam: `src/DCx.Desktop/Media/MediaFrameDescriptionService.cs`, `src/DCx.Desktop/ContentViewerWindow.xaml.cs`, `src/DCx.Desktop/MainWindow.xaml`, `src/DCx.Desktop/MainWindow.xaml.cs`, `scripts/validate-p07.ps1`.
- Identity, mutation, malformed-inventory, state-store and selection denial coverage: `tests/DCx.Core.Tests/LocalModels/OllamaLocalClientTests.cs`, `tests/DCx.Core.Tests/LocalModels/LocalModelSelectorTests.cs`, `tests/DCx.Core.Tests/LocalModels/LocalModelSelectionStoreTests.cs`.
- Exact-counter installed-hardware evidence: `scripts/validate-p06.ps1`.
- Already-installed Whisper registration, full-license review and native UIA fixture: `src/DCx.Core/LocalModels/WhisperBackendRegistration.cs`, `src/DCx.Desktop/LocalModelsWindow.xaml`, `src/DCx.Desktop/LocalModelsWindow.xaml.cs`, `src/DCx.Desktop/MainWindow.xaml.cs`, `scripts/validate-p06-whisper-registration.ps1`, `tests/DCx.Core.Tests/LocalModels/WhisperBackendRegistrationTests.cs`.
- Exact executable/model/WAV lease and local-path hardening: `src/DCx.Core/LocalModels/WhisperCliTranscriber.cs`, `tests/DCx.Core.Tests/LocalModels/WhisperCliTranscriberTests.cs`.
- Accessible direct-WAV consumer and cancel/cleared-binding UIA: `src/DCx.Desktop/ContentViewerWindow.xaml`, `src/DCx.Desktop/ContentViewerWindow.xaml.cs`, `src/DCx.Desktop/MainWindow.xaml.cs`, `scripts/validate-p06-whisper-registration.ps1`, `tests/DCx.FakeWhisperCli/Program.cs`.
- Exact FFmpeg executable/license/signature review and state: `src/DCx.Core/LocalModels/FfmpegBackendRegistration.cs`, `src/DCx.Desktop/LocalModelsWindow.xaml`, `src/DCx.Desktop/LocalModelsWindow.xaml.cs`, `tests/DCx.Core.Tests/LocalModels/FfmpegBackendRegistrationTests.cs`, `docs/architecture/adr/0034-explicit-ffmpeg-file-registration.md`.
- Approved FFmpeg execution, composed MP3/MP4 transcription, migrated frame capture and denial coverage: `src/DCx.Core/LocalModels/FfmpegMediaProcessor.cs`, `src/DCx.Desktop/Media/MediaFrameDescriptionService.cs`, `src/DCx.Desktop/ContentViewerWindow.xaml`, `src/DCx.Desktop/ContentViewerWindow.xaml.cs`, `tests/DCx.Core.Tests/LocalModels/FfmpegMediaProcessorTests.cs`, `tests/DCx.FakeWhisperCli/Program.cs`, `scripts/validate-p06-whisper-registration.ps1`, `scripts/validate-p07.ps1`.
- Decision, development and human-AT records: `docs/architecture/adr/0023-explicit-persisted-local-model-approval.md`, `docs/deployment/DEVELOPMENT.md`, `docs/accessibility/P06_LOCAL_MODELS_CHECKLIST.md`.

## Blockers and unblock condition

- The native fake CLI proves the broker contract, not transcription accuracy, license suitability or real resource use. No approved Whisper executable/model exists on the machine.
- The embedding mock proves the bounded loopback contract, not retrieval quality, actual dimensions, license suitability or real memory/latency. No installed model advertises embeddings.
- The exact approved FFmpeg boundary is wired for frame and MP3/MP4 audio extraction, but the real host candidate remains unsigned GPLv3 software that requires owner/legal review, acceptable provenance and real quality/resource benchmarks before production approval or any distribution decision. It was approved only inside disposable validation data and remains `approved=false` in the host inventory evidence.
- The digest-qualified alias and before/after runtime checks bind the runtime-reported bytes and reviewed license text used for the measured call; they do not establish publisher authenticity, signed provenance, legal license approval or a production model install/update/revocation policy. The accessible manager selects only already-installed local models and deliberately performs no installation.
- Automated UIA passed, but the dedicated JAWS/NVDA/Narrator checklist has not been run and cannot be inferred from automation.
- The registration path proves safe consent and execution for already-installed files, but this host has no approved real Whisper executable/model to select. Install/download of whisper.cpp and a suitable embedding model, with source, version, license and SHA-256 recorded, requires owner approval.
- After approval, run fixed speech accuracy/word-error fixtures and retrieval recall/ranking fixtures, measure CPU/GPU/RAM/VRAM, latency, cancellation and unload under normal and low-resource emulation. P06 remains BLOCKED until those real gates pass.
- No cloud Ollama endpoint, API key or network model call was used.
