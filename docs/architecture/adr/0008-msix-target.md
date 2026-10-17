# ADR 0008: Target signed per-user MSIX packaging

- Status: Accepted provisionally
- Date: 2026-10-17

DCx targets a signed x64 MSIX package installed per user. MSIX best matches the current unelevated application, declared package identity, deterministic install/uninstall, controlled updates and rollback expectations. WSL is not part of the customer runtime. The package must declare and validate its .NET Desktop and WebView2 prerequisites or use a reviewed bootstrap path without silently elevating.

MSI/WiX remains a fallback only if clean-VM evidence shows that MSIX cannot support a required DCx broker, prerequisite or enterprise deployment scenario. A portable ZIP is useful for deterministic staging tests but is not an installer and is not a fallback release channel.

The decision remains provisional because no signing identity or clean Windows 11 VM was supplied. DCx must not distribute an unsigned package. Final acceptance requires authorized certificate custody, signed install/update/uninstall and rollback tests, SmartScreen/AV results, SBOM/vulnerability review and human assistive-technology acceptance.

Update on 2026-10-17: the x64 Windows SDK 10.0.19041 MakeAppx and SignTool binaries were found outside `PATH` and passed Microsoft Authenticode validation. MakeAppx now produces a deterministic unsigned local-validation MSIX as described in ADR 0032. SignTool was not invoked, and the package remains non-distributable and uninstalled.
