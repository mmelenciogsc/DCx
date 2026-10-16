# Privacy model

## Defaults

- Local by default; no user content is contributed or synchronized outward.
- Community contribution opt-in is unchecked and every artifact requires review and approval.
- The global catalog downloads inward as signed content; it cannot read the private store.
- Provider calls disclose the exact payload class and destination before first use and whenever sensitivity changes.
- Usage receipts are minimal and pseudonymous; they contain no prompt, source, artifact body, browser URL history, or file path.

## Data classes

| Class | Default location | Default retention | External transfer |
|---|---|---|---|
| Browser profiles/history | Windows user profile | User-controlled | Website traffic only |
| Task prompts/files | Local private store | Configurable | Task-scoped explicit provider use only |
| API/OAuth credentials | Windows protected secret store | Until revoked | Destination provider only |
| Private CKB/CCB | Local private namespace | User-controlled | Never by default |
| Global replica | Local read-only namespace | Current plus rollback | Download from global service |
| Contribution staging | Local quarantine | Until submitted/deleted | Item-level approval only |
| Usage receipts | Local journal + server minimal record | Policy-defined | Qualified event fields only |
| CARE ledger | Authoritative service | Accounting/legal policy | PayPal and contributor views as required |

Retention periods, export/deletion behavior, telemetry consent, regional requirements, and a production privacy notice remain P10/P14 legal-review gates.

