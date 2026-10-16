# ADR 0003: Separate private and global CKB/CCB stores

- Status: Accepted
- Date: 2026-10-17

Private user CKB/CCB metadata, vectors, and artifacts occupy a distinct writable namespace. The global replica is read-only to ordinary client code and advances only through signed atomic releases. Contribution staging is a third quarantine namespace. No query, backup, synchronization, or migration path may merge these stores implicitly. This separation is a testable security boundary, not a UI label.

