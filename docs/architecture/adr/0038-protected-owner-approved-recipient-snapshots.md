# ADR 0038: Protected owner-approved recipient snapshots

- Status: Accepted for local owner approval; provider verification and payout use blocked
- Date: 2026-10-17

## Context

Payout previews already bind an immutable snapshot ID and SHA-256 of the exact destination, but DCx had no accessible way to prepare that binding without placing the recipient email in ordinary application storage. Treating owner approval as PayPal eligibility would also collapse two different controls and could allow an unverified recipient into a payout.

## Decision

The Windows client provides a keyboard- and UIA-accessible CARE recipient window. It validates and displays the exact contributor pseudonym, recipient email and destination digest. Any input change invalidates review. Saving and revocation each require a distinct, initially unchecked approval.

The exact destination and its complete binding record are stored only as a current-user Windows generic credential under an opaque random snapshot target. The local JSON index contains the random snapshot ID, contributor pseudonym, destination digest, protected-credential target, lifecycle state and UTC audit times—never the email. Active contributors and destination digests are unique. A two-step `pending`→`active` state permits restart recovery to remove an orphaned credential. Revocation first makes the snapshot unusable, then deletes its protected credential while retaining minimized audit metadata.

The protected and ordinary records must match exactly when resolved. Tamper, missing secret, unsupported schema, reparse path, duplicate active identity, interrupted write and conflicting lifecycle state fail closed. The resulting directory deliberately returns `IsVerifiedAndEligible = false`. This owner workflow cannot mark PayPal eligibility, acquire PayPal credentials, contact a provider or initiate a payout.

## Consequences

- Plaintext recipient email is absent from the application metadata file, payout preview, validation evidence and normal logs.
- The current-user Credential Manager and application runtime lease are now security dependencies. This is local Windows protection, not proof of production key-management, retention or PayPal eligibility.
- Metadata and protected credentials are excluded from the current DCx backup roots. Restore cannot silently recreate a recipient binding without its protected secret.
- Exact email necessarily exists transiently in WPF and managed credential strings; secure erasure of immutable managed strings is not claimed.
- Credentialed provider verification, recipient notice/retention policy, human JAWS/NVDA/Narrator acceptance and authorized sandbox payout acceptance remain blocked.
