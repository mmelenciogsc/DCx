# Release staging and deployment

DCx has no production release. The current validated outputs are an unsigned self-contained x64 staging ZIP and an unsigned synthetic-identity MSIX for local validation. Neither is a distributable installer or release.

## Reproduce local staging evidence

From native PowerShell 7.4 or newer running on .NET 10 on Windows (the gate rejects legacy Windows PowerShell before doing work):

```powershell
Set-Location D:\DCx
pwsh.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\validate-p13.ps1
```

The validator performs a locked Release build, reliability tests, two self-contained `win-x64` publish runs, path/length/SHA-256 comparison, two fixed-timestamp ZIP builds, two real MakeAppx package/unpack validations, strict ZIP64 timestamp canonicalization, CycloneDX and license inventory, scoped secret scans, current-signature Microsoft Defender no-remediation scans of the exact publish, ZIP and MSIX, an official NuGet direct/transitive advisory audit, real Windows trust denial, and a real offline WebView2/UIA startup. Generated evidence stays under ignored `artifacts/test-results/p13-*` directories.

The payload embeds .NET Core and Windows Desktop 10.0.12. It does not bundle WebView2 and therefore requires a compatible Evergreen WebView2 Runtime, which is already present on the tested Windows 11 host. DCx restricts detection and environment creation to the Stable channel, rejects channel-suffixed/noncanonical version metadata, and presents a keyboard/UIA-accessible fail-closed prerequisite region when the Runtime is missing or unverifiable. It can copy the exact official consumer address. A separate initially unchecked, one-use approval can hand only that address to the OS default browser after a fresh missing-runtime probe. DCx itself makes no download, runs no installer and requests no elevation; automated UIA uses a no-op adapter. The user independently completes or declines Microsoft's flow and returns to choose Recheck. The MSIX has only a synthetic local-validation identity, no signature, no WebView2 bootstrapper, no configured update service or update activation, and no tested install/uninstall behavior. Real Windows trust inspection returns `NotSigned` and denies it.

## Controlled update admission boundary

The core admits an inert update only after verifying a canonical ECDSA P-256 manifest, a newer four-part MSIX version, a short validity window, an exact-origin HTTPS package, byte length/SHA-256 and injected package signature/identity/publisher/version evidence. The HTTP adapter refuses redirects, cookies, proxies, automatic decompression, unreviewed media types and oversized responses. Staged bytes are never executed or installed.

The native **Updates** surface separates Check from Stage. Check requests only the signed manifest and displays a digest-bound exact review. Stage remains disabled until review and requires a separate initially unchecked approval. It re-downloads and revalidates the manifest, requires exact review/current-version equality, and only then retrieves and verifies inert package bytes. The package and exact signed manifest are completed inside a unique incoming directory; one same-volume rename publishes an exact two-file final set. Missing, extra, changed and reparse members are denied, concurrent stagers converge on one verified set, and partial transfer publishes nothing. Approval resets after one use. Normal product composition reports `not_configured`: there is no production origin or signing root and no update is inferred.

Tests use an ephemeral manifest key, synthetic update bytes and an injected package-trust verifier through a separately environment/root-gated in-memory source. UIA proves denied staging creates no storage; accepted staging publishes one directory containing exactly `package.msix` and `manifest.json`, with no residual incoming directory; network, installer, elevation, activation, restart and production actions remain zero. Unit cases prove partial-transfer cleanup, complete-set tamper denial and concurrent convergence. A separate bounded Windows Authenticode adapter supplies real negative evidence for both the original incomplete fixture and the complete unsigned MakeAppx local-validation package, but no authorized signed-positive package has passed it and no installation API is wired. There is no production feed, protected signing-root custody/rotation, activation or rollback. A future installer must reverify the final package and manifest immediately before activation; review or staging is not enduring install authority.

After staging, the native surface exposes a second default-unchecked request for one read-only final-integrity review. It makes no network request, reopens the exact two-file set, revalidates the retained signature/current policy and package trust, and returns one digest-bound receipt with `ExecutionPermitted=false`. The request resets after use and exposes no path, command or installer method. This proves that the reusable final-file verifier is composed; it does not satisfy install/update acceptance and cannot be consumed later as activation authority. Any future approved installer must repeat final verification while preventing replacement and immediately before its transactional activation/rollback step.

## Backup and restore constraint

The tested recovery service includes browser history/bookmarks and private CKB/CCB. It deliberately excludes WebView2 profiles, cookies, browser sessions, global replicas and Windows Credential Manager records, including the P09 trust-policy revision anchor. Archives are deterministic and integrity checked, but current backup/restore is for quiescent application data only. DCx must be closed; live SQLite/WAL snapshotting is not implemented. Production recovery must define how a fresh replica is securely synchronized after OS/credential loss without weakening the protected revision floor.

## Required path to a distributable build

1. Obtain explicit approval for a production package identity, code-signing operation and any separately selected security-assessment tools. The installed SignTool must not be used before that approval.
2. Establish protected code-signing certificate custody and a reviewed version/update policy.
3. Replace the synthetic identity, validate the approved default-browser WebView2 prerequisite flow and its actual per-user/machine-wide outcomes on clean systems, decide whether a separately authorized bootstrapper is necessary, and produce a genuinely signed per-user x64 MSIX.
4. Run signed install, offline/online startup, upgrade, rollback and uninstall on clean Windows 11 VMs.
5. Re-run the existing Defender/NuGet gates and complete independent vulnerability/native-binary and AV/SmartScreen assessment against the exact signed artifacts.
6. Complete JAWS, NVDA and Narrator acceptance with recorded human results.

Do not publish either staging artifact or describe either as installable. Deployment to the Ubuntu server, external publication, signing, installation and production activation require separate owner approval.

## CARE service deployment constraint

Schema v13's registered period/allocation subledger is validated only in the local SQLite sandbox. It does not authorize deployment or establish who may close a production earning period. Before any Ubuntu deployment, define authenticated close/adjustment roles, MFA/TLS and secret custody, distributed transaction/fencing behavior, approved carry-forward/dispute/tax policy, retention and contributor rights, PayPal sandbox credentials/eligibility, centralized audit/alerting and rollback. Re-run credentialed sandbox and multi-instance acceptance only after explicit owner approval. No server write, container, credential use, PayPal call or payout was performed for this checkpoint.
