# P06 — Local models and resource inventory

- Status: BLOCKED (vision and fallback policy pass; transcription and embedding backends absent)
- Implementation commit: `463bfb6`

No model or runtime was downloaded or installed. The benchmark uses software and models already present on this Windows machine.

## Measured inventory

- Windows GPU: NVIDIA GeForce RTX 4060 Laptop GPU; 8,188 MiB VRAM; driver 610.78.
- Ollama: Windows-native 0.40.0 at `D:\\Programs\\Ollama\\ollama.exe`.
- Installed vision candidates: Qwen3-VL 2B (about 1.9 GB), 4B (about 3.3 GB) and 8B (about 6.2 GB).
- Selected candidate: `qwen3-vl:4b-instruct`, model ID beginning `6bf7b924e48a`, 4.4B parameters, Q4_K_M, vision capability, 262,144 reported context. Ollama's installed model metadata begins with Apache License.
- FFmpeg: Windows-native 2025-03-24 git build with CUDA/NVDEC/NVENC support.
- Whisper: no `whisper-cli` and no FFmpeg Whisper filter found.
- Embeddings: no installed Ollama model advertises an embedding capability; P08 validates storage/search with deterministic vector fixtures but does not claim local embedding generation.

## Implemented

- Loopback-only Ollama HTTP adapter for `/api/tags` and non-streaming `/api/chat` vision requests.
- Image size bound of 20 MiB, deterministic temperature, bounded output tokens, cancellation and explicit unload (`keep_alive: 0`).
- Hardware-aware selection policy chooses the largest installed vision model whose stored size fits within 70% of measured VRAM. Tests cover the 8 GB selection, constrained fallback and no-fit case.
- Mock HTTP tests verify local-only enforcement, model discovery, base64 image transport and response usage/duration parsing.
- Native benchmark creates a deterministic local PNG containing a blue square, red circle and `DCX 42`, performs local inference, validates all three facts, records hardware/model/timing measurements and unloads the model.

## Reproduction

```bash
cd /mnt/d/DCx
./scripts/verify-p00.sh
powershell.exe -NoProfile -ExecutionPolicy Bypass -File D:\\DCx\\scripts\\validate-p06.ps1
```

## Measured result

- Locked Release build: 0 warnings, 0 errors; 3.51 seconds.
- Tests: passed 55, failed 0, skipped 0; about 1 second.
- Vision response: `A blue square and a red circle are displayed above the text "DCX 42".`
- Prompt tokens: 1,100; output tokens: 20.
- Ollama-reported duration: 18,768,089,600 ns; wall duration: 20,992 ms.
- Ollama-reported loaded model and VRAM allocation: 3,527,545,978 bytes each.
- Explicit model unload request: passed.
- Transcription gate: BLOCKED because no approved local Whisper backend is installed.
- Embedding gate: BLOCKED because no approved local embedding model is installed or benchmarked.

## Known limitations and unblock condition

- This single synthetic image proves the adapter and current machine path, not production VLM accuracy. P07 adds media fixtures; broader evals remain necessary.
- The 70% selector uses stored model size as a conservative first filter; real peak memory, context and concurrency benchmarks must remain authoritative.
- Install/download of whisper.cpp and a suitable embedding model (each with recorded hash/license) requires owner approval. After approval, run fixed transcription and retrieval fixtures, record accuracy, memory and latency, then update this phase from BLOCKED only if both pass.
- No cloud Ollama endpoint, API key or network model call was used.
