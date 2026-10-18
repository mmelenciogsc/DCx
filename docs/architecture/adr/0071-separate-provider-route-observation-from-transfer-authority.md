# ADR 0071: Separate provider-route observation from transfer authority

- Status: Accepted for explicit read-only P06 audit; product-session follow-up is ADR 0072
- Date: 2026-10-18

## Context

The authenticated P06 catalog contains nine deduplicated source objects. Production transfer correctly denies redirects until current provider behavior is reviewed, but an ordinary browser or automatically redirecting client would hide the actual route, signed query and regional CDN dependency. Downloading payloads merely to discover routing would prematurely exercise the acquisition path and incur hundreds of megabytes of unapproved transfer.

Route observation is evidence, not transfer authority. It must not silently contact providers during application startup or preflight, retain signed URL queries, read response bodies, mutate the redirect policy or imply that a dynamic CDN route is stable for every user and region.

## Decision

Provide a separate PowerShell 7 audit that requires the explicit `-ExplicitReadOnlyNetworkAudit` switch and loads the authenticated embedded catalog through `DCx.Core`. It derives the exact nine-item plan, issues only `HEAD`, disables automatic redirects, proxy, cookies, credentials, preauthentication and decompression, bounds headers/time and manually follows at most three structurally safe HTTPS redirects. Every request receives identity encoding and no-store directives.

Signed query values exist only in the request URI held by `HttpClient`. Evidence records only query-free origin/path, whether a query existed and its character count. A final serialization guard rejects any query delimiter. Response bodies are never read. Failures use one closed audit code without persisting exception text.

The audit never creates or changes `LocalModelDependencyRedirectPolicyReview`. Its output may support a later owner-reviewed policy decision, but cannot approve or start transfer, extraction, installation, registration, selection or execution.

## Consequences

- Current direct and redirect behavior can be reproduced without downloading the 1.28 GB plan.
- Dynamic, regional or signed redirect behavior remains visible without retaining bearer-like query material.
- A passing route observation proves only status, structure and available HEAD metadata at one time/location. It does not prove payload digest, contained licenses, CDN permanence or global portability.
- ADR 0072 later adds a separate product-side, exact, query-free, two-hour in-memory route-review session. The audit itself still grants no product or payload authority.
