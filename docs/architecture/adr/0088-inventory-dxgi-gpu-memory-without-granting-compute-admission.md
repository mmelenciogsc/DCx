# ADR 0088: Inventory DXGI GPU memory without granting compute admission

- Status: Accepted for Windows hardware inventory; non-NVIDIA compute admission remains blocked
- Date: 2026-10-19

## Context

P06 previously obtained current GPU memory only from NVIDIA NVML. Registry evidence exposed other adapters but not a current budget. Treating static capacity as available memory would over-admit models. Conversely, treating a Windows DXGI budget as proof that Ollama can execute on that adapter would conflate resource inventory with compute-backend acceptance.

Microsoft's `CreateDXGIFactory1`, `IDXGIAdapter3::QueryVideoMemoryInfo` and `DXGI_QUERY_VIDEO_MEMORY_INFO` documentation was rechecked from the official URLs in `DCX_REFERENCES.md` on 2026-10-19; all three returned HTTP 200. The contract identifies `Budget` as the process budget and `CurrentUsage` as current process usage. It is not a reservation or peak measurement.

## Decision

DCx loads only the Windows System32 `dxgi.dll` through a fixed P/Invoke search policy. It enumerates at most 16 adapters, excludes software adapters, queries node zero's local segment and derives remaining budget as `max(0, Budget - CurrentUsage)`. Names are bounded and control-free. Duplicate names are rejected from DXGI matching. Device IDs, subsystem IDs, LUIDs, native handles, reservations and usage history are not returned or retained.

DXGI data is best effort. Unsupported interfaces, failed native calls and malformed or unbounded values produce no DXGI observation. No shell, WMI process, helper executable or network request is used.

Every graphics observation carries an explicit `LocalModelGpuAdmissionEligible` value. Current NVIDIA NVML free-memory observations are eligible because that exact host path has separate Ollama GPU execution evidence. DXGI-only observations are inventory-only and ineligible. Model selection considers only explicitly eligible current-memory observations; an integrated Intel or unvalidated AMD/NVIDIA DXGI budget cannot silently authorize GPU work.

The accessible hardware summary announces the source and whether each current budget is eligible or inventory-only. The current validation host exposes one Intel DXGI-only budget and one separately NVML-qualified NVIDIA adapter.

## Consequences

- Windows users receive current cross-vendor memory-budget inventory without running a diagnostic subprocess.
- The new inventory does not claim GPU compatibility, reserve memory, measure peak usage or weaken the prior NVIDIA admission rule.
- A future Intel/AMD admission path requires a separately approved backend, exact real execution/quality/resource/unload evidence and a new policy decision.
- Resource observations remain volatile and must be recaptured under the inference lease before admitted work.

## Alternatives rejected

- WMI, PowerShell or vendor command-line tools: they add child-process, parsing and execution surfaces.
- Use every DXGI budget for model admission: resource availability does not prove a compatible or approved compute backend.
- Hide non-NVIDIA adapters until compute support exists: this withholds useful installer/resource inventory and makes the limitation harder to understand.
