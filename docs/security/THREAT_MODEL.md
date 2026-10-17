# Threat model

## Protected assets

User files and prompts; browser sessions; API/OAuth/PayPal credentials; private CKB/CCB; Windows input/focus; local process authority; global signing keys and catalog; contribution provenance; usage receipts; entitlement and CARE ledgers; payout approvals; audit evidence.

## Trust boundaries

| Boundary | Untrusted side | Trusted enforcement |
|---|---|---|
| WebView2 content ↔ client | Web pages, scripts, downloads, DOM text | Origin-aware browser adapter and download quarantine |
| Documents/media ↔ parsers | Files, URLs, embedded objects, OCR text | Type/size limits, sandboxing, safe parsers, output validation |
| Model output ↔ tools | Prompt injection, hallucinated arguments | Typed schemas, policy engine, capability grants, evidence checks |
| UI ↔ Windows broker | Agent action requests | Allowlists, safe argument binding, focus lease, approval/cancel |
| Client ↔ OpenAI | Optional approved task payload | Redaction, minimization, protected key storage, TLS, cancellation |
| Client ↔ global service | Catalog updates/contributions/receipts | Signatures, nonce/idempotency, minimization, quarantine |
| Global service ↔ PayPal | Webhooks, entitlements, payouts | Signature verification, reconciliation, owner approval, sandbox first |

## Priority threats and required controls

1. **Prompt injection causes an OS or financial side effect.** Content-derived instructions have no capabilities. The policy engine requires a typed user-intent grant; destructive, external, privileged, publishing, and financial actions require accessible confirmation.
2. **Focus theft or conflicting input harms the user.** A single control coordinator issues revocable agent focus leases. Any user input pauses agent input. Handback restores a recorded UIA element when safe and otherwise announces a deterministic fallback.
3. **Browser session theft.** WebView2 profiles are DCx-owned, ACL-protected, and never created from Chrome cookies. Secrets are excluded from logs, artifacts, screenshots, crash reports, and model context.
4. **Command injection.** Brokers use executable plus argument-array contracts, canonical paths, workspace scopes, time/output limits, and deny shell interpolation by default.
5. **Malicious dependencies or community code.** Pin packages, record hashes/licenses/SBOM, scan, build in quarantine, test permissions, sign approved artifacts, support revocation.
6. **Tampered global updates.** Verify a pinned trust root, manifest signature, artifact digest, monotonic version, expiry/revocation, and atomic swap; preserve rollback.
7. **Private-data exfiltration.** Upload defaults off. Contributions require item-level preview, ownership attestation, redaction and secret/PII/license scans. Telemetry excludes prompts and source content.
8. **Receipt/payout fraud.** Entitlement-bound signed receipts, replay prevention, anomaly checks, audit sampling, immutable journal semantics, double-entry reconciliation, holds, and final owner approval.
9. **Local denial of service/cost runaway.** Per-task token, time, memory, process, network and retry budgets; model unload; cancel/kill only the scoped child process.
10. **Confused-deputy elevation.** The main app runs unelevated. A future elevation helper must be narrowly scoped, short-lived, separately authenticated, and invoked only after explicit consent.

## Implemented OpenAI/Codex controls through P04

- The child environment is rebuilt from a small OS-variable allowlist; arbitrary parent secrets are not inherited.
- DCx uses an isolated per-user `CODEX_HOME` and ephemeral app-server auth storage, so it does not copy or silently reuse the developer's Codex/ChatGPT session.
- API keys live in Windows Credential Manager, never command arguments or project files. Known values and bearer credentials are redacted from bounded child diagnostics.
- P04 threads are ephemeral and read-only; web search, apps and multi-agent features are disabled. Unhandled server tool requests receive no authority.
- Commercial ChatGPT-plan sign-in remains compiled unavailable until eligibility is evidenced. A model catalog is never considered authorization.

## Implemented BOSS controls through P05

- Task graphs reject duplicate/unknown dependencies and cycles before execution.
- Routing is capability- and privacy-constrained with deterministic-first precedence; unavailable private tasks do not silently route to OpenAI.
- Evidence, attempt, elapsed-time, task-token and aggregate-token gates fail closed. The aggregate breaker prevents later capability execution after overrun.
- Checkpoints record decision metadata and measurements but omit task prompt/output bodies.
- Capability exceptions are classified without persisting exception messages that could contain private content.

## Implemented content controls through P07

- Local admission canonicalizes paths, rejects direct reparse files, applies per-kind size ceilings and verifies binary signatures before viewer/parser dispatch.
- Office ZIP/XML extraction caps entry count and expanded data, prohibits DTDs and external XML resolution, and never executes embedded code or objects.
- OCR-derived geometry and repaired semantics must validate before export; display-only content is not silently converted into an editable field.
- YouTube admission requires HTTPS, an exact supported host and a valid video identifier; credentials, lookalikes and unsupported page types are denied.
- Media does not autoplay. FFmpeg receives typed arguments and a bounded timeout; temporary frames are removed. Frame descriptions use only loopback Ollama and leave playback paused for user-controlled resume.
- HTML/DOCX exports are newly generated semantic artifacts rather than modified untrusted source packages. Human review remains necessary before relying on OCR or model descriptions.

## Implemented private-knowledge controls through P08

- Private profile identifiers map to distinct hashed directories; databases, vectors and artifacts are never queried across those roots.
- Optimistic version checks and SQLite transactions prevent silent lost updates. Resource bodies and vectors are bounded and validated before insertion.
- Content-addressed writes verify declared SHA-256 and use same-directory atomic moves. CCB reads repeat artifact integrity, package identity and provenance checks.
- CCB ZIPs reject traversal, absolute/backslash paths, duplicates, symbolic links, excessive entry/expanded size, malformed manifests and missing entrypoints.
- CCB publisher trust is pinned by subject-public-key info. Canonical ECDSA signatures bind code digest and meaningful metadata; an untrusted publisher, changed body or changed package fails closed.
- Artifact packages are data, not executable authority. Only a host-registered adapter with matching identity/digest and passing bounded fixtures becomes a BOSS capability; package code is not dynamically loaded.
- FTS input is tokenized into a bounded parameterized expression. All relational writes and queries use parameters rather than concatenated user content.

## Security gate policy

Security tests must cover denial paths as well as successful paths. A mock or sandbox pass proves only that path. Production OpenAI/PayPal eligibility, code signing, legal review, external penetration testing, and the Ubuntu production deployment remain explicit blockers until separately evidenced.
