# ADR 0008: Target signed per-user MSIX packaging

- Status: Accepted provisionally
- Date: 2026-10-17

DCx targets a signed x64 MSIX package installed per user. MSIX best matches the current unelevated application, declared package identity, deterministic install/uninstall, controlled updates and rollback expectations. WSL is not part of the customer runtime. The package must declare and validate its .NET Desktop and WebView2 prerequisites or use a reviewed bootstrap path without silently elevating.

MSI/WiX remains a fallback only if clean-VM evidence shows that MSIX cannot support a required DCx broker, prerequisite or enterprise deployment scenario. A portable ZIP is useful for deterministic staging tests but is not an installer and is not a fallback release channel.

The decision is provisional because MakeAppx, SignTool, MSIXMgr and WiX are absent, no signing identity exists, and no clean Windows 11 VM was supplied. DCx must not distribute an unsigned package. Final acceptance requires authorized tooling, certificate custody, signed install/update/uninstall and rollback tests, SmartScreen/AV results, SBOM/vulnerability review and human assistive-technology acceptance.
