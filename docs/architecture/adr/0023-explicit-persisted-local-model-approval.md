# ADR 0023: Require explicit persisted approval for installed local models

- Status: Accepted for already-installed local models
- Date: 2026-10-17

## Context

Digest-bound inference prevents a mutable Ollama tag from silently changing model bytes, but environment variables are not an accessible per-user approval mechanism. They do not let a keyboard or screen-reader user review the exact installed identity and license, make separate vision and embedding choices, or reliably retain and revoke those choices.

## Decision

DCx provides a keyboard- and UIA-accessible local-model window that reads only the loopback Ollama inventory. It exposes the exact name, model digest, size, capabilities, complete license text and license-text SHA-256. The approval checkbox begins unchecked and resets after every saved choice. Vision and embedding selections are independent. The window can refresh, select or clear already-installed bindings, but cannot download, install, retag, delete or upload a model.

Immediately before saving, DCx requires exactly one reviewed name with the same digest, capability and license digest. The per-user schema-v1 store persists only name, model digest, license digest and approval UTC. It is bounded, strict about unknown/future fields, rejects mutable names and unsafe/reparse state paths, and replaces state through a same-directory write-through temporary file.

Every inference again requires the persisted name, model digest, license digest and capability both before and after the request. A changed identity, capability or license rejects the result.

## Consequences

- Model selection is reachable without environment editing and is exposed through stable UI Automation identifiers.
- Consent is explicit and capability-specific; no model is silently selected by default.
- The complete license is inspectable, while only its digest is persisted.
- Malformed or unsafe state fails closed rather than silently selecting a model.
- The manager does not prove publisher identity or legal license suitability and is not an installer or updater. Signed provenance, revocation and approved acquisition remain separate blocked gates.

## Alternatives rejected

- Environment-only configuration: inaccessible, easy to misconfigure and not a durable review record.
- Friendly-name selection: mutable or duplicate names do not identify model bytes.
- Persisting full license text: unnecessary per-user duplication and greater retention than the binding requires.
- Automatic best-model selection: bypasses the user's explicit identity and license review.
