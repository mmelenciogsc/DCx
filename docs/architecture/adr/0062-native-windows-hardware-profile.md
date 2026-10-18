# ADR 0062: Profile local-model resources through bounded native Windows evidence

- Status: Accepted for base inventory; static selection superseded by ADR 0086 and DXGI inventory extended by ADR 0088
- Date: 2026-10-18

## Context

P06 validation measured the development laptop through PowerShell and `nvidia-smi`, while the product UI independently treated every unique digest-bound model with a matching capability as eligible. That meant a model exceeding DCx's documented 70% graphics-memory policy could still be approved from the desktop. WSL must not be required for customers, and resolving an arbitrary diagnostic executable from `PATH` would create another execution boundary.

## Decision

Add a native Windows hardware source inside DCx.Core. It reads physical/available memory through `GlobalMemoryStatusEx`, processor identity through the read-only machine registry, process architecture through the runtime, application-data volume capacity through `DriveInfo`, and at most 16 graphics adapters from bounded read-only `HKLM\SYSTEM\CurrentControlSet\Control\Video` entries. It spawns no process, invokes no shell, uses no network and persists nothing.

The profiler validates bounded text and plausible capacities, rejects available values above totals, requires graphics-memory values to name their exact registry source, removes exact duplicate adapter records and orders adapters deterministically. The UI deliberately labels these values `registry-reported graphics memory`; it does not misrepresent integrated aperture/shared values as dedicated VRAM. Stable PCI/device identifiers and registry paths are not returned or displayed.

The original decision used the largest reported graphics-memory value for the 70% vision prefilter and physical RAM for its 25% embedding limit. ADR 0086 supersedes that static rule with current NVIDIA free-memory and current available-system-memory admission. ADR 0088 adds bounded DXGI process-budget inventory but explicitly keeps DXGI-only observations ineligible until their compute backend is separately validated. The desktop rereads the profile and inventory before approval, and production execution recaptures eligible resources under the inference lease.

## Consequences

- Hardware/resource evidence is available inside the installable Windows product, through keyboard-focusable UIA text, without WSL or an external utility.
- The current laptop exposes the RTX 4060 Laptop GPU, Windows driver `32.0.16.1078` and 8,585,740,288 registry-reported bytes; independent validation still retains the NVIDIA tool's driver/VRAM observation for comparison.
- Product approval can no longer bypass the resource prefilter merely because a model advertises a capability.
- Registry data and static model size are selection prefilters, not proof of free VRAM, GPU execution, peak working set, latency, quality or unload. The approved benchmark gate remains mandatory, and changing resource availability after approval must still fail any future execution policy that requires a tighter live check.
- Arbitrary same-user or administrative code is outside this evidence boundary. This decision creates no install, download, model-use or remote-provider authority.
