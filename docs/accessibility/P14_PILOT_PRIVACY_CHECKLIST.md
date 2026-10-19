# P14 human screen-reader checklist — readiness and local metric privacy

Status: NOT RUN. Automated Windows UIA evidence does not satisfy this checklist.

Run separately with installed JAWS, NVDA and Narrator. Use a disposable DCx data root and generated aggregate values only. Do not enroll a real user, connect an event source or network transport, or activate production.

| Screen reader | Version | Keyboard/read-order result | Consent/status result | Scaling/contrast result | Tester/date |
|---|---|---|---|---|---|
| JAWS | UNVERIFIED | UNVERIFIED | UNVERIFIED | UNVERIFIED | UNVERIFIED |
| NVDA | UNVERIFIED | UNVERIFIED | UNVERIFIED | UNVERIFIED | UNVERIFIED |
| Narrator | UNVERIFIED | UNVERIFIED | UNVERIFIED | UNVERIFIED | UNVERIFIED |

For each screen reader:

1. Open **Release readiness**. Confirm focus reaches the `DCx release readiness` heading and the `NO GO` decision, seven-blocker summary and 12 metric definitions are announced in a useful order.
2. Confirm the local metric status announces disabled, zero daily aggregate buckets and no pilot collection or network transport.
3. Read the complete notice with ordinary review commands. Confirm the excluded prompts, responses, URLs, paths, keystrokes, screen content, account identifiers and user identifiers are understandable without character review.
4. Confirm both approvals start unchecked. Confirm **Enable local aggregate recording** and **Stop recording and purge local aggregates** are unavailable until their applicable approval is checked.
5. Use `Alt+I`, then `Alt+E`. Confirm the enabled result receives focus, the one-use approval resets, the status says notice version 1 and zero buckets, and nothing implies pilot enrollment or production activation.
6. Use `Alt+A`, then `Alt+S`. Confirm the permanent-deletion wording is announced before action, the completion receives focus, the approval resets, and status returns to disabled with zero buckets.
7. Use `Alt+R`. Confirm refresh preserves the disabled/zero-bucket state and does not move focus unexpectedly or repeat the full lists.
8. Read the **Local pilot administration preview** status. Confirm it clearly says enrollment, product event sources, network metric transport and production activation are unavailable. Confirm the staged plan announces `blocked, not enrolled`, four future stages, zero enrollment controls and no approved cohort size or schedule. Read all four stage audience/entry/exit descriptions in order and confirm none sounds like an active cohort. Confirm the recent aggregate list exposes only day/key/dimension/count/sum/minimum/maximum values.
9. Use `Alt+D`. Confirm the seven-scenario incident stop drill result receives focus, says external actions zero and requires a fresh readiness decision. Review the results list without losing position.
10. Confirm no control named **Activate production**, **Enroll pilot**, **Send metrics** or equivalent exists. Confirm no login, credential, destination, participant or account field appears.
11. Repeat with keyboard only, 200% scaling and Windows High Contrast. Check that notices reflow, all content remains reachable through the scroll viewer, focus is clear, lists remain navigable and color is not the only state cue.
12. Record any delayed/duplicate live announcement, inaccessible disabled-state explanation, focus loss, access-key conflict, ambiguous purge language or preview language that implies real pilot authority as a release-blocking defect.

P14 remains `NO_GO` until this human gate and the separate policy, enrollment, deployed-administration, human incident-readiness and production-approval gates pass.
