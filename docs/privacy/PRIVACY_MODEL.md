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
| Recovery archive | User-chosen local path | User-controlled | Never by default |

Retention periods, export/deletion behavior, telemetry consent, regional requirements, and a production privacy notice remain P10/P14 legal-review gates.

## Current private-store realization

P08 stores each local profile in a different hashed directory beneath the current Windows user's application-data root. Queries open only the selected profile database; no sync or upload code is present. Content-addressed artifacts remain inside that same namespace. Current confidentiality relies on inherited Windows per-user ACLs; the SQLite database is not represented as encrypted at rest.

P10 adds local contribution preparation but keeps it off by default and provides no upload action. A prepared item contains only the exact content the user reviewed and remains quarantined. Usage receipts intentionally have no prompt, body, browser URL or file-path field; rejection/qualification logs store decision codes rather than private content.

P13 recovery archives include browser history/bookmarks and private CKB/CCB only. WebView2 profiles, cookies, authenticated sessions, global replicas and Windows Credential Manager secrets are excluded. Archives are not encrypted by the current implementation, so the user-selected destination must be protected appropriately; encryption, retention UI and secure deletion remain release blockers.
