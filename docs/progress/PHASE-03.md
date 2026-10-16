# P03 — External native apps and terminal broker

- Status: In progress
- Commands/results: none
- Changed files: none
- Commit: none

Required gate: restricted PowerShell/process/UIA contracts, manual takeover, and destructive-operation denial tests.

## Initial plan and assumptions

Define broker contracts and policy before launching any native process. Start with allowlisted unelevated Calculator/Notepad and a repository-owned demo app, explicit executable/argument arrays, working-directory scopes, output/time limits and deny rules for shell interpolation, elevation, deletion and unapproved paths. Add a focus lease and user takeover UI analogous to P02. Use native UI Automation patterns; pixel input is not part of the validated path. No install/elevation or destructive command will be attempted.
