# ADR 0001: WPF and WebView2 native shell

- Status: Accepted
- Date: 2026-10-17

## Context

DCx requires a native Windows 11 UI, WebView2, mature keyboard/UIA behavior, native Windows build evidence from WSL interop, and no WSL dependency for customers. The host has .NET SDK 10.0.401, Windows Desktop runtime 10.0.12, WPF templates, WebView2 Runtime 155.0.4283.45, and Windows SDK 10.0.19041.0. It has no WinUI templates or .NET workloads, and its only Visual Studio installation is Build Tools 2019.

Official documentation verified on 2026-10-17 describes WPF as a Windows-only .NET UI framework, WinUI 3 as the Windows App SDK UI framework, and WebView2 as supporting .NET/Win32 hosts. See `docs/references/VERIFICATION_LOG.md`.

## Decision

Use WPF on .NET 10 with the Microsoft WebView2 WPF control. Target `net10.0-windows10.0.19041.0`; ordinary users run a native Windows executable. Store each WebView2 user-data profile under DCx's own local application-data tree. Run as the invoking user (`asInvoker`).

## Why this is the current best fit

- It builds now through the supported Windows SDK without installing privileged tooling.
- WPF exposes established UI Automation peers and keyboard/focus primitives needed for JAWS, NVDA, and Narrator.
- WebView2 provides the required live Chromium viewport without importing another browser's cookies.
- The choice minimizes initial installer/runtime complexity while preserving clean adapters around the shell.

## Consequences

- UI-thread discipline and WebView2 lifecycle tests are release-critical.
- Native styling must be intentionally maintained rather than inherited from WinUI 3.
- Automated UIA tests do not replace human screen-reader testing.
- Revisit only if a measured WPF limitation blocks a required gate and a WinUI 3 toolchain can be installed with approval.

