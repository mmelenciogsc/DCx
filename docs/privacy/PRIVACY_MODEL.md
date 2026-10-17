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
| Minimized crash reports | Per-user local application data, default off | Maximum 20; purge on opt-out or explicit delete | No transport implemented |
| Pilot metric aggregates | Local SQLite, default off | Purged on local opt-out; final policy pending | No transport implemented |

Retention periods, export/deletion behavior, telemetry consent, regional requirements, and a production privacy notice remain P10/P14 legal-review gates.

## Current private-store realization

P08 stores each local profile in a different hashed directory beneath the current Windows user's application-data root. Queries open only the selected profile database; no sync or upload code is present. Content-addressed artifacts remain inside that same namespace. Current confidentiality relies on inherited Windows per-user ACLs; the SQLite database is not represented as encrypted at rest.

P10 adds local contribution preparation but keeps it off by default and provides no Windows-client upload action. A prepared item contains only the exact content the user reviewed and begins quarantined. The separately gated simulation API transports that exact reviewed metadata and optional artifact in a bounded envelope; it does not enumerate private-store content. Contributor status is owner-scoped, while an authenticated reviewer can inspect exact pending items as required for review. A second default-off worker gate persists only operation/submission identifiers, a stable failure code, bounded count and retry/dead-letter/remediation timestamps; its queue has no candidate, prompt, artifact, exception message or stack field. Child-process diagnostics retain neither bearer tokens nor candidate bodies, and rejection/qualification records use stable codes rather than matched private values. These test-only paths do not authorize production transfer. Usage receipts intentionally have no prompt, body, browser URL or file-path field.

P13 recovery archives include browser history/bookmarks and private CKB/CCB only. WebView2 profiles, cookies, authenticated sessions, global replicas and Windows Credential Manager secrets are excluded. Archives are not encrypted by the current implementation, so the user-selected destination must be protected appropriately; encryption, retention UI and secure deletion remain release blockers.

P13 minimized crash diagnostics also default off under a versioned notice. An enabled report contains only schema version, UTC time, a closed component category, a closed error category, a terminating flag and application version. The schema has no exception message, stack, dump, prompt, response, URL, path, keystroke, account or user identifier. Atomic bounded files inherit the per-user application-data protection and are capped at 20. The accessible UI can delete reports without changing consent; opt-out purges all reports. No export or network transport exists, and future transfer is not authorized by local consent.

P14's pilot metric store persists aggregate counts, sums, minimums and maximums by UTC day, reviewed metric key and closed enum dimension. Its schema has no raw-event timestamp, prompt/output body, URL, local path, keystroke, field value, private artifact body, provider account identifier or user identifier. Recording defaults off; consent notice versions cannot roll back; local opt-out purges aggregates. No product event source or network transport is connected. This implementation is not authorization to collect metrics: an approved notice, accessible enrollment/withdrawal, final retention/deletion policy and explicit pilot approval remain blocked.
