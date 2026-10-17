# Architecture decision index

| ADR | Decision | Status | Date |
|---|---|---|---|
| [0001](architecture/adr/0001-wpf-webview2.md) | Use WPF on .NET 10 with WebView2 for the native shell | Accepted | 2026-10-17 |
| [0002](architecture/adr/0002-local-first-trust-boundaries.md) | Enforce local-first execution and explicit broker boundaries | Accepted | 2026-10-17 |
| [0003](architecture/adr/0003-separate-private-and-global-knowledge.md) | Keep private and global CKB/CCB stores physically and logically separate | Accepted | 2026-10-17 |
| [0004](architecture/adr/0004-openai-auth-and-codex-boundary.md) | Isolate Codex runtime and keep API-key mode independent from gated ChatGPT-plan sign-in | Accepted | 2026-10-17 |
| [0005](architecture/adr/0005-embedded-sqlite-private-store.md) | Use embedded SQLite FTS5 plus bounded managed vectors for private CKB/CCB | Accepted | 2026-10-17 |
| [0006](architecture/adr/0006-signed-global-delta-chain.md) | Synchronize the global replica as a signed, checkpointed, atomically activated delta chain | Accepted | 2026-10-17 |
| [0007](architecture/adr/0007-care-integer-ledger-and-live-disable.md) | Use integer-cent CARE accounting and structurally disable live payouts | Accepted for sandbox | 2026-10-17 |
| [0008](architecture/adr/0008-msix-target.md) | Target signed per-user MSIX packaging; retain MSI only as an evidence-driven fallback | Accepted provisionally | 2026-10-17 |
| [0009](architecture/adr/0009-offline-root-trust-policy.md) | Rotate and revoke global release keys through an offline-root-signed policy chain | Accepted | 2026-10-17 |
| [0010](architecture/adr/0010-closed-app-recovery-lease.md) | Run accessible recovery separately under an exclusive per-data-root lease | Accepted | 2026-10-17 |

Decisions are revisited only when measured constraints or authoritative requirements change. Superseded ADRs remain in the repository.
