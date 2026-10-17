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
   ```

5. Continue only the earliest uncompleted phase. Record commands, results, changed files, blockers, and the milestone commit in its phase document. Never infer manual screen-reader or external-service success from automated tests.

Current implementation includes the native browser, shared-control and Windows brokers, isolated Codex provider boundary, BOSS orchestration, a loopback-only Ollama vision adapter, and the validated P07 local content/document/media surface. P00–P05 and P07 pass their stated automated gates; P06 is blocked only on the absent, unapproved Whisper backend. P07's structured HTML/DOCX exports, OCR/geometry, local frame description and hostile-input tests are at `c618254`; human assistive-technology testing, universal YouTube playback/frame capture and transcription are not claimed. P08 private local CKB/CCB storage is active. Consult the status and P08 phase document before continuing.
