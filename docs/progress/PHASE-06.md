# P06 — Local models and resource inventory

- Status: BLOCKED (real vision passes; transcription/embedding contracts pass fixtures; actual backends remain absent)
- Implementation commits: `463bfb6`, `b4aa4c9`

No model or runtime was downloaded or installed. The real benchmark uses only software and models already present on this Windows machine.

## Measured inventory

- Windows GPU: NVIDIA GeForce RTX 4060 Laptop GPU; 8,188 MiB VRAM; driver 610.78.
- Ollama: Windows-native 0.40.0 at `D:\Programs\Ollama\ollama.exe`.
- Installed vision candidates: Qwen3-VL 2B (about 1.9 GB), 4B (about 3.3 GB) and 8B (about 6.2 GB).
- Selected candidate: `qwen3-vl:4b-instruct`, SHA-256 model digest `6bf7b924e48a8e3b754d2efb9de9dd949a866a51644bca35ff0c33dd72f8ca16`, 4.4B parameters, Q4_K_M, and advertised completion/vision/tools capabilities.
- Inspected model license begins `Apache License`; license-text SHA-256 is `d0fa3710cf6cf8e18009051c28988e4aa8114b0d4cd6e3f102bb9ccba50d7cc7`.
- FFmpeg: Windows-native 2025-03-24 git build with CUDA/NVDEC/NVENC support.
- Whisper: no installed `whisper-cli` detected.
- Embeddings: all installed Ollama models were inspected through loopback `/api/show`; zero advertise `embedding`.

## Implemented

- Loopback-only Ollama adapter with bounded JSON responses for model inventory, `/api/show`, nonstreaming vision and `/api/embed`.
- Embedding requests allow at most 32 bounded texts, disable truncation, explicitly unload, require request/response model and count agreement, and reject empty, inconsistent, oversized or nonfinite vectors.
- Image size/prompt bounds, deterministic temperature/output limit, cancellation and explicit unload.
- Hardware-aware vision selection uses at most 70% of measured VRAM. Embedding selection prefers the smallest installed model within 25% of measured RAM; low-resource no-fit tests fail closed.
- Fallback order is validated local GPU, bounded local CPU, then an explicitly approved reachable remote provider only for nonprivate input. Private input with no local backend becomes unavailable.
- Hash-pinned `whisper-cli.exe` and `.bin`/`.gguf` model boundary. It accepts bounded signature-checked WAV only, uses argument arrays and a scrubbed environment, drains but never retains child diagnostics, enforces timeout/tree kill, bounds transcript output, records working set and removes its operation directory.
- A native fake Whisper CLI validates process/argument/output/cancellation behavior. It does not count as speech-recognition accuracy evidence.

## Reproduction

```bash
cd /mnt/d/DCx
powershell.exe -NoProfile -ExecutionPolicy Bypass -File D:\DCx\scripts\validate-p06.ps1
powershell.exe -NoProfile -ExecutionPolicy Bypass -File D:\DCx\scripts\ci.ps1
```

## Measured result

- Locked Release build: 0 warnings, 0 errors; 3.00 seconds in the recorded P06 run.
- P06 local-model tests: passed 17, failed 0, skipped 0; 470 ms.
- Expanded full suite: passed 134, failed 0, skipped 0; about 1 second.
- Vision response: `A blue square and a red circle are displayed above the text "DCX 42".`
- Prompt tokens: 1,100; output tokens: 20.
- Ollama-reported duration: 18,957,929,100 ns; wall duration: 18,964 ms.
- Ollama-reported loaded model and VRAM allocation: 3,527,545,978 bytes each.
- Explicit model unload request: passed.
- Hash-pinned fake Whisper happy path, model-tamper denial, invalid-WAV denial and timeout/tree-kill passed.
- Ollama inspection/embed request shape, capability/license parsing, two-vector response and inconsistent-dimension denial passed.
- Hardware evidence: `artifacts/test-results/p06-79f3de92af194ebeb21d1e7383e7dd4e/hardware-manifest.json` (generated and intentionally not committed).

## Blockers and unblock condition

- The native fake CLI proves the broker contract, not transcription accuracy, license suitability or real resource use. No approved Whisper executable/model exists on the machine.
- The embedding mock proves the bounded loopback contract, not retrieval quality, actual dimensions, license suitability or real memory/latency. No installed model advertises embeddings.
- Install/download of whisper.cpp and a suitable embedding model, with source, version, license and SHA-256 recorded, requires owner approval.
- After approval, run fixed speech accuracy/word-error fixtures and retrieval recall/ranking fixtures, measure CPU/GPU/RAM/VRAM, latency, cancellation and unload under normal and low-resource emulation. P06 remains BLOCKED until those real gates pass.
- No cloud Ollama endpoint, API key or network model call was used.
