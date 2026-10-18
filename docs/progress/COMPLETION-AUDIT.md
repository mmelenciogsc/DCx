# DCx requirement-to-evidence completion audit

Updated: 2026-10-18

This audit maps the authoritative handoff to current executable evidence. “Automated gate passed” does not imply production readiness, human assistive-technology acceptance, provider approval or deployment. The product objective is not complete while any required phase remains partial or blocked.

Authoritative inputs: `DCx_Codex_Handoff_Kit/README.md`, `DCX_PRODUCT_ARCHITECTURE.md`, `DCX_PHASED_BUILD_AND_VALIDATION.md` and `DCX_REFERENCES.md`. Detailed commands, commits, changed files and limitations remain in the linked phase records.

| Phase | Required proof | Current evidence | Audit finding |
|---|---|---|---|
| P00 | Actual Windows/WSL inventory, reproducible repository/build, ADR/security baseline | [PHASE-00.md](PHASE-00.md) | Local gate achieved. Remote workflow remains intentionally unrun without a configured Git remote. |
| P01 | Native WebView2 shell, profiles, browser functions, keyboard/UIA and manual AT checklist | [PHASE-01.md](PHASE-01.md) | Automated native gate achieved. Human JAWS/NVDA/Narrator entries remain unverified. |
| P02 | Shared live viewport, manual takeover/handback, safe DOM/ARIA actions and focus restoration | [PHASE-02.md](PHASE-02.md) | Automated native fixture gate achieved; live third-party authenticated acceptance remains separate. |
| P03 | Restricted PowerShell/process/UIA broker and keyboard-operable native-app handoff | [PHASE-03.md](PHASE-03.md) | Automated native gate achieved; elevation/install authority remains absent by design. |
| P04 | Per-user Codex app-server, API-key path, cancellation/reconnect, eligible ChatGPT sign-in only | [PHASE-04.md](PHASE-04.md) | Offline/native mock gate achieved. Real provider invocation and commercial ChatGPT sign-in eligibility remain blocked. |
| P05 | BOSS task graph, eight roles, deterministic-first routing, budgets, checkpoints and measured benchmark | [PHASE-05.md](PHASE-05.md) | Deterministic benchmark gate achieved; fixture metrics make no production cost-savings claim. |
| P06 | Hardware-selected Ollama/Whisper/embedding stack with approved payloads, licenses and real quality/resource benchmarks | [PHASE-06.md](PHASE-06.md) | Partial. Hardware policy, exact review/staging boundaries and real installed vision benchmark pass. Owner-approved transfer plus Whisper/embedding installation and acceptance remain blocked. |
| P07 | Files/documents/media viewers, scanned-PDF wizard/export, visual Q&A and accessibility evidence | [PHASE-07.md](PHASE-07.md) | Automated native gate achieved. Human AT, real speech accuracy and live/authenticated/protected YouTube acceptance remain unverified. |
| P08 | Private isolated CKB/CCB, vector/metadata/artifact stores, provenance and hostile artifact denial | [PHASE-08.md](PHASE-08.md) | Automated local/native gate achieved. |
| P09 | Authoritative global service plus signed incremental two-client synchronization and local replica separation | [PHASE-09.md](PHASE-09.md) | Partial. Signed loopback service/client and hostile storage gates pass. Approved Docker/Compose deployment, production key custody and stronger same-user isolation remain blocked. |
| P10 | Default-off item review, scanning, sandbox validation, governance, publication, withdrawals and fraud-resistant receipts | [PHASE-10.md](PHASE-10.md) | Partial authenticated local simulation. Production identity/attestation, hardened deployed sandbox and publication authority remain blocked. |
| P11 | Subscription/webhook/accounting/CARE/Net60/holds/refunds, payout sandbox, immutable audit and transparent contributor status | [PHASE-11.md](PHASE-11.md) | Partial protected local sandbox. Schema-v14 close approval, exact allocations/liability, recipient workflow, operations and a default-off subject-bound loopback transport over the integrity-verified contributor projection pass 121/121 plus native UIA. Production contributor identity/TLS/deployment, credentials, exact plan, merchant/recipient eligibility, production policy and authorized sandbox acceptance remain blocked. |
| P12 | Reproducible full vertical flows plus separate live/human acceptance | [PHASE-12.md](PHASE-12.md) | Partial. Nine safe fixtures pass; live Facebook/QuickBooks/YouTube, real signed install, deployed CCB and real PayPal acceptance remain absent. |
| P13 | Signed install/update/uninstall lifecycle on clean Windows, WebView2 bootstrap, scans, recovery and human AT signoff | [PHASE-13.md](PHASE-13.md) | Partial. Reproducible unsigned artifacts, Defender/NuGet/trust denial, offline startup and recovery pass. Signing identity, clean VM, independent assessment and human AT remain blocked. |
| P14 | Opt-in pilot, admin/metrics/runbooks/policies/approvals, all major issues triaged and explicit go/no-go | [PHASE-14.md](PHASE-14.md) | Local controls pass, but pilot/admin/provider/policy gates remain incomplete. Current decision is `NO_GO`; seven blockers and production activation false. |

## Cross-cutting audit

- Actual Windows execution: proved by native Release builds, WPF/WebView2/UIA processes and retained TRX/evidence; Linux-only results are not used for Windows claims.
- Accessibility automation: keyboard focus, UIA names/roles and live announcements are covered throughout. Human JAWS/NVDA/Narrator signoff is missing and remains release-blocking.
- Private-data and contribution defaults: private/global separation and unchecked item approval are covered by P08–P10. No real content was published.
- Privileged/external actions: dependency transfer/install, server deployment, provider credentials, signing/install, live services, real payments and production activation remain unauthorized and were not attempted.
- Release claim: contradicted by P14 evidence. DCx is under construction, not a validated installer, deployed service or approved financial system.

## Current exact-source checkpoint

- Implementation: `eca0617`.
- Full Windows suite: 535 passed, 0 failed, 0 skipped.
- P11: `artifacts/test-results/p11-6cd9d108b3cf486e8ad96c4b38f53fbe/`; 121/121 plus native UIA.
- P13: `artifacts/test-results/p13-6f0aa2e6684240099c734a6cb3414813/`; 23/23 reliability, 21/21 trust, Defender clean, NuGet advisories 0 and unsigned trust denied.
- P14: `artifacts/test-results/p14-b1fae16c0e124cccb2b065bc689c5db7/`; 10/10, seven blockers, `NO_GO`, activation false.

Evidence directories are ignored build artifacts and are intentionally not committed. The phase records are the durable reproduction index.
