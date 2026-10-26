# U02 consent-bound prerequisite handoff: Windows evidence

Date captured: 2026-10-26 UTC

Source SHA: `7a591346f60db937b8ed9dd012a28e4e53c75a6c`

## Native Windows result

The owner ran the following commands from a native Windows CMD session after closing the
previous visible fixture window:

```cmd
cd /d D:\DCx
dotnet build src\DCx.Desktop\DCx.Desktop.csproj --configuration Release --no-restore --disable-build-servers
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\validate-u02-prerequisite-review-ui.ps1
```

The owner reported that the build succeeded and the PowerShell validator completed.  The
generated validator receipt was then read without modification over restricted SSH from:

```text
D:\DCx\artifacts\test-results\u02-prerequisite-review-db507a7824fb4be1b135fd49fdead31a\validation.json
SHA-256 BA01243D26739F118A86AB128A3E91D68912898B8752C73486A5A0EB39DCC9F1
```

The receipt records the guarded fixture denial without its environment variable, the
stable review window and first WebView2 item, one-at-a-time review, initially unchecked
terms/install approvals, six distinct access keys (`T`, `I`, `D`, `R`, `O`, `C`),
decline/retry coverage, and one recorded policy-approved official-resource handoff.

The fixture handoff status says it opened no external application and reset the one-use
install approval.  The receipt records zero external child processes, downloads,
installer starts, elevation requests, and PowerShell invocations.

## Scope limit

This validates the native UI Automation fixture only.  Its handoff adapter is
intentionally non-destructive, so it does not open a browser, download a vendor artifact,
accept vendor terms, start WebView2/Docker/WSL installation, request elevation, or prove
a production bootstrapper.  The owner previously reported that the review UI's live
status is announced by Narrator, JAWS, and NVDA; that human observation is distinct from
this UIA receipt and does not replace formal end-to-end installer accessibility testing.
