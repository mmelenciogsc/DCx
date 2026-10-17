# ADR 0031: require no-remediation release malware and dependency-advisory scans

- Status: Accepted for local release staging; independent assessment remains blocked
- Date: 2026-10-17

## Context

P13 produced reproducible unsigned staging output, an SBOM and license inventory, but those controls did not establish a malware-scan or dependency-vulnerability result. The Windows 11 host already provides Microsoft Defender Antivirus and the installed .NET SDK can query NuGet's official advisory source. Installing another privileged scanner, changing antivirus state or remediating files is outside the current authorization.

A release gate must not inherit API keys or other arbitrary environment values into a scanner, trust a command merely because it has a familiar file name, mutate the staged output, silently accept stale signatures, or treat an absent JSON property as an advisory. It also must not inflate these first-party checks into a claim of independent security review.

## Decision

Before P13 records staging evidence:

1. Require Microsoft Defender Antivirus, antispyware and real-time protection to be enabled, with a nonempty current signature version and `DefenderSignaturesOutOfDate=false`.
2. Resolve `MpCmdRun.exe` beneath the Windows Program Files special folder, reject a reparse file, require a valid Microsoft Windows Authenticode signer, and record SHA-256 identities for the executable and signer certificate.
3. Run custom scans over the exact reproducible publish tree, fixed-timestamp ZIP and canonical unsigned MSIX with `-DisableRemediation`. Use typed arguments, a five-minute deadline per scan, 64 KiB limits on each output stream and a process-tree kill on timeout. A nonzero result fails the gate.
4. Clear the scanner child environment and reconstruct only `SystemDrive`, `SystemRoot`, `WINDIR`, `ProgramData` and `ProgramFiles` from Windows system/special-folder APIs. Do not inherit credentials, API keys, user-profile variables, temporary paths or `PATH`.
5. Run `dotnet list package --vulnerable --include-transitive --format json` for the shipped desktop project. Require the version-1 schema, exact requested parameters, one project, the sole source `https://api.nuget.org/v3/index.json`, output no larger than 1 MiB and zero direct or transitive vulnerability entries.
6. Exercise the audit parser against both an absent-framework zero-result fixture and a three-advisory fixture before applying it to the official response. Malformed affected-package or vulnerability nodes fail closed.

Raw Defender console text is discarded. The ignored evidence directory retains the bounded NuGet JSON response; the committed phase record contains only minimized scanner state, digests, counts and outcomes.

## Consequences

- The current publish tree, ZIP and unsigned MSIX pass Microsoft Defender scans without remediation, using current host signatures.
- The current direct and transitive NuGet closure reports zero vulnerabilities from the official advisory source.
- No scanner can modify, quarantine or delete release files through this gate, and arbitrary parent-process secrets are not propagated to Defender.
- A signature update, Defender configuration change, dependency advisory or parser/schema drift can fail a future run; evidence is point-in-time and must be regenerated for each release candidate.
- These checks are not an independent penetration test, SAST/DAST review, native-binary CVE analysis, second-engine malware assessment, signed-installer result or clean-VM lifecycle test. P13 remains blocked.

## Verified upstream contracts

- Microsoft Defender command-line arguments: <https://learn.microsoft.com/en-us/defender-endpoint/command-line-arguments-microsoft-defender-antivirus>
- Microsoft Defender `Get-MpComputerStatus`: <https://learn.microsoft.com/en-us/powershell/module/defender/get-mpcomputerstatus>
- NuGet package auditing: <https://learn.microsoft.com/en-us/nuget/concepts/auditing-packages>
- .NET package-list command: <https://learn.microsoft.com/en-us/dotnet/core/tools/dotnet-package-list>
