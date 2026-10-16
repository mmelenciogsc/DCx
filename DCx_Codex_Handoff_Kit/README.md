# DCx — Codex CLI handoff kit (2026-10-17)

Start here. This is a **build directive**, not a claim that DCx has been implemented. Copy the complete single paragraph in `CODEX_ONE_PARAGRAPH_PROMPT.txt` into the currently running Codex CLI session under WSL Ubuntu on Windows 11. Provide the other files in the same directory and their paths; the prompt directs Codex to read them. The agent should work phase-by-phase, test each gate, and avoid claiming completion without evidence.

## Files
- `CODEX_ONE_PARAGRAPH_PROMPT.txt` — one-paragraph Codex instruction.
- `DCX_PRODUCT_ARCHITECTURE.md` — product requirements, components, boundaries, data contracts, safety, business rules.
- `DCX_PHASED_BUILD_AND_VALIDATION.md` — implementation sequence and explicit acceptance tests.
- `DCX_REFERENCES.md` — official documentation and validation resources, with URLs and caveats.

## Important execution notes
- Codex currently runs in **WSL**, but DCx targets a **native Windows 11 client**. Codex should inspect the host and choose a supported .NET/WinUI 3 or WPF/WebView2 approach; WSL must not become a runtime dependency for ordinary customers unless separately installed/consented to. Windows build/test must actually execute on Windows, with Windows-specific commands launched via supported interop. Do not confuse successful Linux tests with tested Windows native functionality.
- No credentials or production financial actions are supplied. **Never** invent PayPal or OpenAI approval, API access, or service readiness. Use sandbox/test doubles and flag human prerequisites.
- Never deploy to or alter the owner's Ubuntu server without confirmed access and explicit deployment approval. Deliver deployment manifests and a local test stack first.
- Maintain accessibility and human–agent control handoff as release-blocking requirements.
- No indefinite background promise: run feasible work in the live Codex session and persist honest status for continuation.
