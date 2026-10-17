# Resume DCx implementation

1. Read `DCx_Codex_Handoff_Kit/README.md`, `DCX_PRODUCT_ARCHITECTURE.md`, `DCX_PHASED_BUILD_AND_VALIDATION.md`, and `DCX_REFERENCES.md` in full.
2. Read `AGENTS.md`, `docs/progress/STATUS.md`, the active `docs/progress/PHASE-XX.md`, `docs/DECISIONS.md`, and `git status --short --branch`.
3. Preserve user changes. Do not deploy to the Ubuntu server, install/elevate, publish, use paid APIs, or transact without the required approval.
4. Reproduce the current native Windows baseline:

   ```bash
   cd /mnt/d/DCx
   ./scripts/verify-p00.sh
   pwsh.exe -NoProfile -ExecutionPolicy Bypass -File D:\\DCx\\scripts\\smoke-desktop.ps1
   pwsh.exe -NoProfile -ExecutionPolicy Bypass -File D:\\DCx\\scripts\\validate-p01.ps1
   pwsh.exe -NoProfile -ExecutionPolicy Bypass -File D:\\DCx\\scripts\\validate-p02.ps1
   pwsh.exe -NoProfile -ExecutionPolicy Bypass -File D:\\DCx\\scripts\\validate-p03.ps1
   pwsh.exe -NoProfile -ExecutionPolicy Bypass -File D:\\DCx\\scripts\\validate-p04.ps1
   pwsh.exe -NoProfile -ExecutionPolicy Bypass -File D:\\DCx\\scripts\\validate-p05.ps1
   pwsh.exe -NoProfile -ExecutionPolicy Bypass -File D:\\DCx\\scripts\\validate-p06.ps1
   pwsh.exe -NoProfile -ExecutionPolicy Bypass -File D:\\DCx\\scripts\\validate-p07.ps1
   pwsh.exe -NoProfile -ExecutionPolicy Bypass -File D:\\DCx\\scripts\\validate-p08.ps1
   pwsh.exe -NoProfile -ExecutionPolicy Bypass -File D:\\DCx\\scripts\\validate-p09.ps1
   pwsh.exe -NoProfile -ExecutionPolicy Bypass -File D:\\DCx\\scripts\\validate-p10.ps1
   pwsh.exe -NoProfile -ExecutionPolicy Bypass -File D:\\DCx\\scripts\\validate-p11.ps1
   pwsh.exe -NoProfile -ExecutionPolicy Bypass -File D:\\DCx\\scripts\\validate-p12-fixtures.ps1
   pwsh.exe -NoProfile -ExecutionPolicy Bypass -File D:\\DCx\\scripts\\validate-p13.ps1
   pwsh.exe -NoProfile -ExecutionPolicy Bypass -File D:\\DCx\\scripts\\validate-p14.ps1
   ```

5. Continue only the earliest uncompleted phase. Record commands, results, changed files, blockers, and the milestone commit in its phase document. Never infer manual screen-reader or external-service success from automated tests.

Current implementation includes P06's hardened loopback Ollama, exact-file Whisper registration and leased execution, plus the exact-approved FFmpeg media processor at `acc8e62` and real deterministic extraction gate at `bcfe167`; P09's offline-root-signed trust-policy rotation and per-user protected minimum-policy anchor; P10's path-private, default-off authenticated local contribution simulation and rootless hostile-package gate; the bounded read-only Ubuntu capability audit at `8b94159`; P11's durable local sandbox accounting/reconciliation controls; P12's nine fixture scenarios and inert exact-package review; P13's reproducible unsigned package, recovery, private diagnostics, controlled-update admission and release scans; and P14's default-off aggregate metric store plus shared-policy read-only readiness dashboard. The current native suite passes 289/289. P06, P09–P14 remain honestly blocked as detailed in status; no external production action occurred. No dependency was installed, no real user media or account session was accessed, no server was deployed, and no package was signed, installed or published. Do not install/elevate, change server access, deploy, contact external providers, use credentials, publish, enroll users, subscribe, pay, sign or transact. Resume from the earliest blocker the owner authorizes; do not change the decision to GO while any critical/major blocker remains.

## Latest checkpoint (supersedes older P13 hash/path references above)

- P06 implementation `5cdf5ec` adds accessible exact-file registration for an already-installed Whisper executable, model and two complete licenses. Approval is default-off, all four byte digests are rechecked before atomic save, no download/install action exists, and clearing preserves the source files. Executable/model/WAV leases, local-drive/reparse denial and license revalidation harden execution.
- P06 evidence: `artifacts/test-results/p06-af443c5e68b94ea785eaf979432daf4f/`; 44/44 local-model tests, native registration UIA, production-adapter fake transcript and the installed RTX/Ollama vision benchmark pass. P07 regression evidence: `artifacts/test-results/p07-2d99cd06835848e18b52d4804acf44f2/`. The full suite passes 273/273.
- P06 consumer implementation `ed6f001` wires admitted WAV transcription to the exact approved local binding with accessible cancel, strict bounded transcript text, completed focus and no partial/stale text after cancel or clear. Evidence: `artifacts/test-results/p06-fec5e76f92cb4c49be4cb75bee3aa5c9/`; 46/46 local-model tests. P07 regression: `artifacts/test-results/p07-ee447f4bbdd84aed8fee91999ccc091b/`. The full suite passes 275/275.
- P06 registration milestone `2f71398` added accessible exact-file FFmpeg registration with full license and real Authenticode review; its pending consumer migration was completed by `acc8e62` below. The installed candidate is SHA-256 `943c5c0623ded76eb02e4b593a7ac1bac043fbbcf6d1d34d673fb8c0369fab52`, GPLv3 license digest `8ceb4b9ee5adedde47b31e975c1d90c73ad27b6b165a1dcd80c7c545eb65b903`, `NotSigned`, and unapproved outside disposable tests. Do not approve or bundle it without owner/legal review.
- No real approved Whisper runtime/model or embedding model is installed. Transcription accuracy/provenance/resource benchmarks and human JAWS/NVDA/Narrator acceptance remain blocked; do not install dependencies without explicit approval.
- Implementation `54f66bc` adds Stable/Evergreen-only WebView2 detection and environment creation, rejects nonnumeric channel-suffixed versions, and exposes an assertive keyboard/UIA missing-runtime group. Browser controls fail closed while independent local tools remain available. Copying the reviewed official address and local recheck are the only actions; no profile, download or install occurs.
- P13 evidence: `artifacts/test-results/p13-81759336634347e5a597fadaf387d4ac/`; reproducible ZIP SHA-256 `a2bfb66a44c58b42d54200dc8ffbc437af348128a847f38e0a86430ec993f926`; reproducible unsigned MSIX SHA-256 `7b1d002d2f81a0b03b7ac654ea63bf799c03990b5762ef1021ac2b3d2d2c4072`.
- P14 evidence: `artifacts/test-results/p14-706e90540c21499292134fd03636def5/`; 10/10 readiness tests; seven blockers; decision `NO_GO`; production activation false.
- Current P13 evidence: `artifacts/test-results/p13-069437e31ca041e2b43faa8ad4e15b30/`; reproducible ZIP SHA-256 `fc77b4bb0268a74a9d3ee12efcb55d45dbcd47b39710746ff7dbc95ff554f117`; reproducible unsigned MSIX SHA-256 `1b44e06f2080df13c66a3abf48f11b1045fe44ccd19c7745cbc30a9fc78e20fb`.
- Current P14 evidence: `artifacts/test-results/p14-2380e554d4d9426ba4aa57b112fdf984/`; 10/10 readiness tests; seven blockers; decision `NO_GO`; production activation false.
- Latest P13 evidence after FFmpeg registration: `artifacts/test-results/p13-ca274e0d84a04aba814d886d103ec4dc/`; ZIP SHA-256 `3a59b0b529847187136d35c38c3f7ef5a270fa7c301828db77df9d0a921f87ef`; unsigned MSIX SHA-256 `33aa25d8a52e28b9f0c3424c8b2a467d7607ce33166f628aacc3a8df2386499d`; external FFmpeg is not bundled.
- Latest P14 evidence: `artifacts/test-results/p14-a257f049dfeb4870a76848ad02975026/`; 10/10; seven blockers; `NO_GO`; activation false.
- P06 implementation `acc8e62` adds the bounded exact-approved FFmpeg processor, removes local frame extraction's `PATH` lookup, and composes MP3/MP4 extraction with the approved Whisper boundary. Evidence: `artifacts/test-results/p06-ec2254a02625414ea7fe8eb09ba23453/`; 60/60 local-model tests and native WAV/MP3 registration/consumer UIA. P07 regression: `artifacts/test-results/p07-15ebf4df5ff14865b6e6944ce062fedd/`; full suite 289/289. The real unsigned GPLv3 FFmpeg candidate was used only in disposable validation data and remains unapproved for normal use.
- Prior P13 evidence: `artifacts/test-results/p13-22869c1f183e406aa80c57b108bebf82/`; ZIP SHA-256 `d16e4786a677e3e6cc663107bd6662696469101685e4437f7a5cd9d26cb95530`; unsigned MSIX SHA-256 `2c30bd5068b44aed4ecf0d63b068b0c3ee1f5f34fb5ddfb348817ed5c6c5a18f`; external FFmpeg and per-user approval state are not bundled.
- Prior P14 evidence: `artifacts/test-results/p14-8885b9a242db41878d224f34f0d4a223/`; 10/10; seven blockers; `NO_GO`; activation false.
- P06 benchmark `bcfe167` proves the real installed FFmpeg candidate only through a disposable exact-file approval: a generated 440 Hz MP3 became 32,078-byte mono PCM16/16 kHz WAV in 1,048 ms, measured 439.5 Hz, and was deleted on artifact disposal. Evidence: `artifacts/test-results/p06-87811a7e3abf4db090d236332cf0557b/`; P07: `artifacts/test-results/p07-0a3c791833884f67bfa554162c81538a/`; full suite 289/289. No runnable Whisper backend or Ollama embedding model exists; two pre-existing ADFi `.pt` files are incompatible and were not copied or executed.
- Prior P13 evidence: `artifacts/test-results/p13-027bb6f9f9c74c9faf4029952bcd0f95/`; ZIP SHA-256 `5e5a548a0069bd7a4eb6d6c72feeed6d0ea35bc7af133d45710c80a1c908bdbe`; unsigned MSIX SHA-256 `989947d4d164761ff379c3aba74f3557a7335c04a7c50f3b2e6debdeb8b65aa9`; external FFmpeg and approval state absent.
- Prior P14 evidence: `artifacts/test-results/p14-0a53bc3e66c34fa28fa6f122f8c239aa/`; 10/10; seven blockers; `NO_GO`; activation false.
- Read-only server audit `8b94159`: Ubuntu 24.04, .NET/ASP.NET 10.0.12, Docker 29.5.2, Compose 5.1.4 and Bubblewrap 0.9.0 are present. Docker daemon access and writable cgroup delegation are absent; exact Bubblewrap `--unshare-all` network isolation fails. Remote writes/installs/service changes/containers/deployments were all zero. Evidence: `artifacts/test-results/ubuntu-audit-2e50bf228e12483585cea19ebf05462a/`; local P09: `artifacts/test-results/p09-6bbf54ee910741009db23bf984dbd203/`; P10: `artifacts/test-results/p10-0247c7fdcc5d4e45bcdf2e34680c41d1/`.
- Latest P13 evidence: `artifacts/test-results/p13-7f75d31cd690426f948278d4edc5a49c/`; ZIP SHA-256 `380efaf64889bacc239f30f1d464450141430a732a65195aa00fcba1f7bbd71d`; unsigned MSIX SHA-256 `8bc849c6ba4a78bb810640b591fbeeca96d9fa8e9b77bf9345a91a183cb086e4`.
- Latest P14 evidence: `artifacts/test-results/p14-2e941793f3bc4af48b3a3acc6cebc5c3/`; 10/10; seven blockers; `NO_GO`; activation false.
- P13 still lacks an approved WebView2 bootstrap/install flow, production package identity/certificate, signed clean-VM lifecycle, independent security assessment and human JAWS/NVDA/Narrator acceptance. Do not sign, install or publish without explicit approval.
