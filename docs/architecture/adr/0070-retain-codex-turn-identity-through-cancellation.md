# ADR 0070: Retain Codex turn identity through cancellation

- Status: Accepted for the local app-server boundary; real provider acceptance remains blocked
- Date: 2026-10-18

## Context

Codex app-server notifications can identify an active turn before the correlated `turn/start` response resumes on the client. DCx originally passed the user's cancellation token into the identity-bearing request wait. Under scheduler load, a notification created local active-turn state, the user canceled, and the request wait threw even though its response had arrived or was imminent. DCx then lacked the correlated response path needed to register cancellation and interrupt the exact server turn.

Merely waiting for local active-turn count does not close this ordering race. Cancellation must retain the thread and turn identities required by the documented `turn/interrupt` contract.

## Decision

Honor user cancellation before dispatching `turn/start`. Once dispatch begins, await its identity-bearing response without the user token, while retaining the existing bounded request timeout and app-server lifetime cancellation. Register the user token against the returned exact thread/turn identity; registration immediately invokes interruption if cancellation occurred during the bounded response wait.

Extend the offline app-server fixture with an explicitly enabled bounded delay mode that sends active-turn notifications before the correlated response. The cancellation contract must still return `interrupted` and remove the exact turn. The mode uses no credentials, network or billable inference.

## Consequences

- Cancellation cannot orphan a known active turn merely because notification and response continuations are reordered.
- After `turn/start` dispatch, cancellation may wait up to the configured bounded request timeout to obtain the identity needed for safe interruption.
- App-server lifetime termination and request timeout still fail closed.
- This does not establish a real Windows Codex installation, provider eligibility or credentialed OpenAI acceptance.
