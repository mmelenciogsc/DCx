# ADR 0014: private local crash diagnostics

- Status: Accepted for local controls
- Date: 2026-10-17

## Context

P13 needs enough crash evidence to distinguish broad reliability failures without turning exception text, stack traces, browser state or user content into a second private-data store. DCx has no approved diagnostic service or support-upload policy, and opt-in to local retention cannot imply consent to a later transfer.

## Decision

The normal leased DCx process exposes versioned, default-off local crash retention. Reports contain exactly six fields: schema version, UTC recording time, closed component category, closed error category, process-terminating flag and application version. Exception messages, stack traces, dumps, prompts, outputs, URLs, paths, keystrokes, account identifiers and user identifiers are not represented in the schema.

Reports use bounded atomic JSON files under the per-user application-data root, reject reparse boundaries and retain at most 20. Exception hooks are best effort and never mark the source exception handled. The accessible privacy surface states the exact fields and absence rules, persists an explicit opt-in, offers deletion without changing consent and purges on opt-out. No export or network transport exists.

Recovery and readiness utility processes remain outside this hook because they deliberately operate without the normal process's exclusive data lease. Any future diagnostic transfer requires a separate reviewed notice, exact preview, explicit approval, destination policy and security gate; local retention consent supplies none of that authority.

## Consequences

- The data has lower debugging value than a message, stack or dump, but it avoids retaining likely secrets and user content.
- Store corruption or unsafe filesystem boundaries make diagnostics unavailable without blocking normal browser startup.
- The setting and report count can be operated by keyboard and Windows UI Automation, while human JAWS/NVDA/Narrator acceptance remains a separate P13 gate.
- Production support ingestion, symbolication and crash-rate telemetry remain unimplemented and unauthorized.
