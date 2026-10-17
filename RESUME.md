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
   ```

5. Continue only the earliest uncompleted phase. Record commands, results, changed files, blockers, and the milestone commit in its phase document. Never infer manual screen-reader or external-service success from automated tests.

Current implementation includes the native/browser/broker/provider/BOSS foundation, P07 content/document/media, P08 private CKB/CCB, P09 signed global sync, P10 consent-gated CKB governance and P11's local CARE/PayPal mock model at `2f97540`. P06 is blocked on Whisper/embeddings; P09 on Compose; P10 on a CCB sandbox; P11 on real PayPal sandbox eligibility/credentials and a durable ledger. P12 fixture-only vertical composition is active. Do not install/elevate, contact the owner's server or external providers, use credentials, publish, subscribe, pay or transact. Consult the status and P12 phase document before continuing.
