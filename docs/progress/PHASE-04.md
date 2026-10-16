# P04 — OpenAI identity and local Codex integration

- Status: In progress
- Commands/results: current-model resolver only; no credentials or API calls
- Changed files: none attributable to P04
- Commit: none

Commercial ChatGPT sign-in for DCx distribution is a strict eligibility blocker. API-key mode must remain independent. Fetch the exact official current model/auth/app-server documents before coding this integration.

## Planned gate execution

Verify current official model, API authentication and Codex app-server contracts first. Then implement provider-neutral local contracts, protected configuration boundaries, an offline deterministic provider for CI, independently configurable API-key mode, and only the distribution-approved ChatGPT sign-in path documented by OpenAI. No credential discovery, browser-cookie reuse, real sign-in or paid request is authorized by this phase's current automated path.
