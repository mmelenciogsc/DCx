# Resume DCx implementation

1. Read `DCx_Codex_Handoff_Kit/README.md`, `DCX_PRODUCT_ARCHITECTURE.md`, `DCX_PHASED_BUILD_AND_VALIDATION.md`, and `DCX_REFERENCES.md` in full.
2. Read `AGENTS.md`, `docs/progress/STATUS.md`, the active `docs/progress/PHASE-XX.md`, `docs/DECISIONS.md`, and `git status --short --branch`.
3. Preserve user changes. Do not deploy to the Ubuntu server, install/elevate, publish, use paid APIs, or transact without the required approval.
4. Reproduce the current native Windows baseline:

   ```bash
   cd /mnt/d/DCx
   ./scripts/verify-p00.sh
   powershell.exe -NoProfile -ExecutionPolicy Bypass -File D:\\DCx\\scripts\\smoke-desktop.ps1
   powershell.exe -NoProfile -ExecutionPolicy Bypass -File D:\\DCx\\scripts\\validate-p01.ps1
   powershell.exe -NoProfile -ExecutionPolicy Bypass -File D:\\DCx\\scripts\\validate-p02.ps1
   powershell.exe -NoProfile -ExecutionPolicy Bypass -File D:\\DCx\\scripts\\validate-p03.ps1
   powershell.exe -NoProfile -ExecutionPolicy Bypass -File D:\\DCx\\scripts\\validate-p04.ps1
   powershell.exe -NoProfile -ExecutionPolicy Bypass -File D:\\DCx\\scripts\\validate-p05.ps1
   powershell.exe -NoProfile -ExecutionPolicy Bypass -File D:\\DCx\\scripts\\validate-p06.ps1
   powershell.exe -NoProfile -ExecutionPolicy Bypass -File D:\\DCx\\scripts\\validate-p07.ps1
   powershell.exe -NoProfile -ExecutionPolicy Bypass -File D:\\DCx\\scripts\\validate-p08.ps1
   powershell.exe -NoProfile -ExecutionPolicy Bypass -File D:\\DCx\\scripts\\validate-p09.ps1
   powershell.exe -NoProfile -ExecutionPolicy Bypass -File D:\\DCx\\scripts\\validate-p10.ps1
   powershell.exe -NoProfile -ExecutionPolicy Bypass -File D:\\DCx\\scripts\\validate-p11.ps1
   powershell.exe -NoProfile -ExecutionPolicy Bypass -File D:\\DCx\\scripts\\validate-p12-fixtures.ps1
   powershell.exe -NoProfile -ExecutionPolicy Bypass -File D:\\DCx\\scripts\\validate-p13.ps1
   powershell.exe -NoProfile -ExecutionPolicy Bypass -File D:\\DCx\\scripts\\validate-p14.ps1
   ```

5. Continue only the earliest uncompleted phase. Record commands, results, changed files, blockers, and the milestone commit in its phase document. Never infer manual screen-reader or external-service success from automated tests.

Current implementation includes P06's hardened loopback Ollama and execution-leased Whisper boundaries at `a160435`, P09's offline-root-signed trust-policy rotation plus durable publication-id replay protection at `9123a81`, P10's rootless hostile CCB sandbox and schema-v2 signed propagation at `5c3611e`, bounded ownership-fenced publication recovery at `bd30319`, a default-off authenticated simulation contribution API at `f417f6e`, its separately gated publication retry/dead-letter worker at `0fc2649`, durable pre-sandbox CCB review jobs at `bbadbca`, and bounded process-local request/concurrency pressure at `e13246b`, P11's durable payout crash-window reconciliation at `4b05cc0`, P00–P11's other recorded native/local capabilities and blockers, P12's eight-scenario fixture composition at `10ac274`, P13's accessible closed-app recovery at `3e9b172`, reproducible staging and default-off minimized local crash diagnostics at `1b32cdf`, and P14's default-off aggregate metric store at `66715b0` plus the shared-policy, read-only native readiness dashboard at `edd516a`. The current native suite passes 188/188. P06 and P09–P14 remain honestly blocked as detailed in status; no external production action occurred. P06 has no installed real Whisper or embedding backend. P09 has no executed Compose gate or production root-key custody ceremony. P10's authenticated API requires a digest-bound, body-free, ownership-fenced job before CCB sandbox execution; same-SQLite concurrent workers execute one claim. Reviewer-authorized publication intents remain immutable, idempotent and separately fenced. Retry/dead-letter status contains stable codes and timestamps only, and reviewer remediation cannot change the job or publication binding. Enabled contribution routes have bounded request/concurrency pressure keyed by ephemeral hashes of direct-peer addresses, but that in-memory state resets on restart and is not distributed or proxy-aware. The static bearer-hash API and worker remain behind separate simulation gates. This is not production identity, public ingress, distributed/high-availability queueing, monitoring or an operator console. The Windows client still has no upload action; Ubuntu 24.04, production authentication, seccomp/cgroup-v2/immutable-image controls, monitoring and independent penetration testing remain absent. The combined HTTP-to-real-Linux-sandbox path remains unrun. P11 has no official PayPal adapter, sandbox credentials or eligibility evidence. P13 has no packaging/signing/vulnerability-scanner toolchain, certificate, clean VM or human AT signoff. Its deterministic ZIP is unsigned staging output, not an installer. Crash diagnostics contain no exception content, cap retention at 20 and have no transport. P14's dashboard has no activation control and is not a pilot admin console; there is no approved consent UI, pilot enrollment or metric event/network wiring, and production activation is false. Do not install/elevate, contact external providers or the owner's server, use credentials, publish, enroll users, subscribe, pay, sign or transact. Resume from the earliest blocker the owner authorizes; do not change the decision to GO while any critical/major blocker remains.
