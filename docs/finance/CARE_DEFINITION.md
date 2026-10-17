# CARE distributable-net-profit definition

This is the implemented sandbox calculation, not legal, tax, employment, securities or accounting advice. Production requires counsel and accountant approval.

For each UTC calendar earning month, all inputs are nonnegative integer US-dollar cents:

`distributable net profit = max(0, recognized subscription revenue - refunds and chargebacks - PayPal/payment fees - approved operating costs - approved tax and risk reserve)`

The aggregate contributor pool is `floor(distributable net profit × configured basis points / 10,000)`. Configuration is rejected above 4,000 basis points, so the pool is at most 40% in aggregate—not 40% per contributor. Zero or negative defined net profit produces a zero pool.

The sandbox divides the pool by qualified usage weights after P10 fraud exclusions. Integer-cent rounding uses largest remainder with contributor pseudonym as the deterministic tie-break. Allocation sums exactly to the aggregate pool. Amounts below the configured threshold are held, not sent. Carry-forward, forfeiture and escheat treatment require legal/accounting policy before production.

The earning-period close is the month's final calendar date in UTC. Net 60 eligibility is exactly 60 calendar days after that date. Eligibility does not authorize payment: verified PayPal Payouts account capability, recipient eligibility, reconciliation, no active hold, and explicit owner approval are still required.

The official-contract-shaped sandbox adapter uses stable local/provider batch references. Every owner-approved item binds an immutable recipient-snapshot ID and digest of the exact destination; both the eligible directory record and provider receiver must match it. `PENDING`, `PROCESSING` and pending item states remain reserved. A terminal mixed outcome posts cash and clears payable only for successful items; failed amounts remain payable with exact durable item statuses and no automatic retry. Retry/hold decisions, contributor notifications and later returned/refunded/reversed corrections still require separately approved workflows.

Refunds, chargebacks and adjustments after period close require immutable correcting entries and re-reconciliation. The current in-memory fixture does not implement production close/reopen or cross-period recovery.

The local sandbox reduces entitlement events by signed UTC occurrence time rather than arrival order. A newer event replaces current state; an older event is audited and any independent balanced financial correction is posted, but it cannot regress entitlement. At the same timestamp, deactivation dominates activation. Exact replay is idempotent, while reuse of an event or transmission identifier with different canonical content fails closed. This is a conservative fixture rule, not a claim of credentialed PayPal acceptance. Subscription event mapping, account eligibility, a protected recipient-snapshot/approval workflow, failed-item retry and post-terminal correction policy, and accounting/legal approval remain required.
