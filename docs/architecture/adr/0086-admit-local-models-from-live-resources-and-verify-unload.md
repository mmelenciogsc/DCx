# ADR 0086: Admit local models from live resources and verify unload

- Status: Accepted for NVIDIA-backed local inference; ADR 0088 adds DXGI inventory and ADR 0089 adds shared-runtime overlap detection while other compute admission and exclusive isolation remain blocked
- Date: 2026-10-19

## Context

DCx selected local models from registry-reported graphics capacity and total physical memory. Those static capacities did not show whether enough memory was usable immediately before an operation. Normal vision and embedding requests sent `keep_alive: 0`, but only benchmark execution proved that the exact model had actually disappeared from Ollama. Concurrent pressure could therefore invalidate admission after selection, and a successful response could outlive an unverified loaded model.

## Decision

On Windows, DCx reads NVIDIA device memory through the installed driver's exact `%WINDIR%\System32\nvml.dll`. It never uses DLL search-path resolution, a child process or a network request. Interop is limited to a fixed set of NVML exports, at most 16 devices, strict UTF-8 text and consistent positive total/free/used values. Devices are merged into the bounded registry profile only by one exact unique adapter name. No PCI identifier, serial number or stable device identifier is collected.

Vision admission uses at most 70% of the current NVIDIA free-memory observation. Embedding admission uses at most 25% of current Windows available system memory. Production vision, embedding and benchmark operations recapture this profile after acquiring the per-user Ollama lease and after verifying the exact approved model binding, immediately before the model operation. An exact descriptor that no longer fits is denied before inference. Missing live GPU telemetry denies GPU-backed vision selection; it is not replaced by registry capacity.

Ordinary vision and embedding calls explicitly unload the exact model, then require three consecutive loaded-model inventory observations in which its digest is absent. ADR 0089 strengthens this to require the complete inventory to be empty and to reject, without unloading, an unrelated model. Cleanup has its own bounded deadline and remains inside the inference lease. A failed unload response, malformed inventory, cancellation-cleanup failure or unconfirmed absence fails closed even if inference returned a usable response.

## Consequences

- Selection and execution are bounded by currently reported usable memory rather than installed capacity alone.
- NVIDIA telemetry comes from the already-installed driver and is not bundled with DCx. ADR 0088 adds System32 DXGI process-budget inventory for other adapters, but marks DXGI-only observations ineligible. Intel/AMD GPU admission still requires a separately validated compute backend and real execution/resource/unload evidence.
- Live free memory is a point observation, not a reservation. ADR 0089's bounded quiet fence detects preloaded work and unrelated models present during cleanup but cannot prevent another client from starting inside the operation. Real acceptance still needs an operator-controlled environment or stronger runtime isolation and measured peak use.
- Normal inference cannot report success while exact-model unload remains unconfirmed. This favors resource cleanup and deterministic attribution over returning a response after cleanup failure.
- Evidence may retain adapter name, driver, total/free byte counts and the nonidentifying source label. It retains no prompt, media, PCI address, serial number or NVML device handle.

## Alternatives rejected

- Registry capacity as available VRAM: it is static installed capacity and can materially overstate usable memory.
- Total physical RAM for embedding admission: it ignores current system pressure.
- `keep_alive: 0` without observing absence: request intent is not proof of unload.
- Loading `nvml.dll` by name: search-path resolution creates an avoidable DLL-preloading boundary.
- Querying `nvidia-smi`: a child process adds executable provenance, argument, output and lifecycle risks for data available through the installed driver API.
