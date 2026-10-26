# ADR 0124: Bind prerequisite consent to an exact official external-resource handoff

- Status: Accepted for the U02 policy foundation; installer composition remains blocked.
- Date: 2026-10-27
- Source: owner-approved continuation work and the U02 prerequisite-review validation.

## Context

DCx needs a conventional Windows installation path that can tell a user about missing
WebView2, Docker Desktop, and WSL 2.  The current unsigned synthetic-identity MSIX is
not a production installer, and its existing WebView2 handoff deliberately has no
download, installer, package-manager, or elevation authority.

An acknowledgement checkbox in DCx cannot accept a third party's terms on the user's
behalf, authenticate a vendor payload, or make an interactive administrator prompt
accessible.  Treating it as such would hide a material vendor decision and would make
an untested MSIX route more authoritative than its packaging and validation evidence.

## Decision

The Core prerequisite catalog contains only these reviewed Windows-target entries:

1. Microsoft Edge WebView2 Runtime;
2. Docker Desktop for Windows; and
3. Windows Subsystem for Linux 2.

For a single catalog item, `EvaluateExternalResourceHandoff` may permit a handoff only
when a fresh detection result is exactly `Missing`, both separately recorded consent
checkboxes apply to that exact item, the user has not declined it, and the fixed
official HTTPS resource and terms URIs remain safe and catalog-known.  The returned
resource URI is null for every denied decision.

Even a permitted handoff explicitly grants no DCx authority to download a file, run an
installer or package manager, request elevation, accept terms, or monitor/claim an
installation result.  It may only be consumed by a future narrow Windows shell adapter
that opens that exact official resource for the user.  The vendor-owned browser or
installer remains responsible for its own terms, visible prompts, accessibility, and
elevation.

## Consequences and next gate

- This prevents one prerequisite's consent from being reused for another and prevents
  dynamic, redirected, query-bearing, or unreviewed resource definitions from becoming
  a launcher target.
- The existing normal WebView2 recovery flow remains unchanged.  The new policy is not
  normal-product installer composition and does not make the U02 fixture an installer.
- A future production bootstrapper requires a separately reviewed packaging choice,
  versioned vendor-artifact acquisition and signature/hash policy, accessible visible
  vendor terms/elevation handling, cancellation/rollback behavior, clean Windows 11
  VM validation, and fresh Narrator/NVDA/JAWS acceptance.  It must not silently pass
  quiet, license-acceptance, or package-manager agreement switches.
- Docker Desktop and WSL setup remain blocked until that bootstrapper work and its
  physical Windows evidence exist.  No Docker, WSL, driver, or vendor installer was
  started by this decision.

## Evidence

`PrerequisiteBootstrapPolicyTests` covers each catalog item and every denied detection
or consent state, including an unsafe official URI.  The focused Core policy suite,
including the older WebView2 handoff policy, passed 16/16 on 2026-10-27.  The separate
native U02 review-only UI validation previously reported zero downloads, installers,
elevation requests, and PowerShell invocations; it is not evidence of installation.
