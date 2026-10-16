# ADR 0004: OpenAI authentication and local Codex boundary

- Status: Accepted
- Date: 2026-10-17

## Context

DCx needs a per-user local Codex app-server and can support user-provided OpenAI API keys. It may offer ChatGPT-plan usage only through an officially documented and distribution-approved path. DCx is intended to be commercial; OpenAI's current Sign in with ChatGPT documentation describes selected commercial/private participation and directs paid or remotely hosted apps to an interest form.

## Decision

- Run `codex.exe app-server` locally over redirected stdio JSONL, never through a remotely exposed listener.
- Use a DCx-owned per-user `CODEX_HOME`, ephemeral Codex credential storage and API-only enforcement for the initial production path.
- Store the API key in Windows Credential Manager. Transmit it to the child only through the documented `account/login/start` message and redact it from all surfaced diagnostics.
- Discover models from `model/list`; a catalog entry is not treated as entitlement proof.
- Keep ChatGPT sign-in disabled in distributed UI until OpenAI approval is verified. Do not reuse browser cookies, the user's unrelated Codex profile or this development session.
- Start P04 turns ephemeral and read-only with no approval prompts, web search, apps or multi-agent capability. Later tool use must pass DCx's own broker and approval policy.

## Consequences

API-key mode is testable and deployable independently. The authenticated ChatGPT-plan branch remains unavailable rather than implying eligibility. An offline process fixture validates protocol lifecycle without credentials, network traffic or cost. A real Windows Codex installation and owner-authorized provider call remain external validation work.
