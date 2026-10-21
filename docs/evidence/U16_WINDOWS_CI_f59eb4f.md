# U16 Windows CI evidence — `f59eb4f`

Date: 2026-10-22

## Bound source and execution context

| Item | Value |
|---|---|
| Branch | `codex/ubuntu-ui-continuation-u00` |
| Source SHA | `f59eb4fe825e132048dc15823a12a623d09f5cf2` |
| GitHub remote ref | `refs/heads/codex/ubuntu-ui-continuation-u00` resolved to that SHA before Windows execution. |
| Windows checkout | `D:\DCx`, clean before and after the run, tracking the same remote SHA. |
| Windows SDK selected by `global.json` policy | `10.0.402` (allowed by `latestPatch` for pinned `10.0.401`). |
| Command | `powershell.exe -NoProfile -ExecutionPolicy Bypass -File D:\DCx\scripts\ci.ps1` |

## Observed result

The repository CI created `D:\DCx\artifacts\test-results\p00-tests.trx`.

| TRX fact | Value |
|---|---|
| Last write UTC | `2026-10-21T20:31:44Z` |
| Size | `981786` bytes |
| SHA-256 | `a65f2dc274674680050fbe6a329d68da4d5e56fbb7cd9751d2bbcaeeeb8d9acb` |
| Result | **FAILED**: 701 executed; 691 passed; 10 failed; 0 error/timeout/aborted. |

All ten failures occur in Windows Credential Manager cleanup. Each reports `Windows Credential Manager could not delete the credential` at `WindowsCredentialVault.Delete` line 118. The affected tests are two direct vault tests and eight Global Knowledge/Local Models vault-anchor tests. Their save/read portions reached the delete call; no unrelated assertion failure appears in the TRX.

This invocation ran through restricted noninteractive SSH. That mechanism cannot establish an interactive desktop or a user credential-store session, and `cmdkey /list | findstr /I DCx` returned no visible `DCx` test target after the process completed. This is evidence of the specific remote-context limitation, not proof that no credential-store residue exists. No source change, test skip, or weakened credential policy was made.

## Known invocation correction

An earlier attempt to wrap `scripts/ci.ps1` in a PowerShell logging pipeline incorrectly escaped `$LASTEXITCODE`; it stopped at restore and produced no test result. The direct command above supersedes that harness error. A direct diagnostic `dotnet restore DCx.slnx --locked-mode --verbosity normal` passed with zero warnings/errors before the final CI run.

## Required next validation

Run the same SHA and command in a real logged-in Windows desktop session with an approved foreground harness. Capture the resulting TRX checksum and manually verify that the test-only Credential Manager targets are absent afterward. Until then, U16 native CI is **BLOCKED**, not PASS.
