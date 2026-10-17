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
   ```

5. Continue only the earliest uncompleted phase. Record commands, results, changed files, blockers, and the milestone commit in its phase document. Never infer manual screen-reader or external-service success from automated tests.

Current implementation includes P00–P11's recorded native/local capabilities and blockers plus P12's seven-scenario fixture composition at `1c51f9d`. P06, P09–P13 remain honestly blocked as detailed in status; no external production action occurred. P13's audit found no packaging/signing/SBOM toolchain, certificate, clean VM or human AT signoff. Do not install/elevate, contact external providers or the owner's server, use credentials, publish, subscribe, pay, sign or transact. Resume from the earliest blocker the owner authorizes; do not mark P14 ready while these gates remain.
