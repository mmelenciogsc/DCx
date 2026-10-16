# P02 — Agent/browser shared control

- Status: In progress
- Commands/results: none
- Changed files: none
- Commit: none

Required gate: repeatable agent/manual-user/agent handoff with focus restoration, no conflicting input, and security tests.

## Initial plan and assumptions

Implement an explicit control-state machine (`User`, `AgentPending`, `Agent`, `UserTakeover`, `Paused`, `Cancelled`) before enabling any browser action. DOM/ARIA reads and actions will use typed same-WebView contracts, origin/navigation-version checks and per-action evidence. Any physical user input revokes the agent input lease. The controlled fixture will simulate authentication: agent navigates, user types the approval field manually, then explicitly hands control back for the agent to continue. No real credentials or Facebook automation are used in the P02 CI path.
