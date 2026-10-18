# ADR 0082: Inspect only the current Facebook post after explicit user handback

- Status: Accepted for controlled local validation; live Facebook acceptance remains blocked
- Date: 2026-10-18

## Context

Finding an old post requires an authenticated third-party session, login/MFA/consent decisions and site navigation that DCx must not impersonate. A search result page, login page, checkpoint or challenge is not evidence of a post. Page-controlled canonical metadata is also not sufficient evidence that the currently displayed address is a direct post.

## Decision

Keep the workflow in DCx's existing shared WebView2 viewport and profile. DCx may open a bounded Facebook search URL, but the user completes login, MFA, consent and candidate selection, then explicitly confirms handback. Inspection is bound to the current navigation generation and address.

Accept a production result only when the exact HTTPS host is Facebook, the current address has a supported direct post/video/permalink shape, and bounded visible article semantics contain post evidence. Normalize the result to `www.facebook.com`, remove query and fragment tracking, and treat visible page text as untrusted bounded data. Never read password values, cookies, browser storage or a Chrome/Edge profile. Never infer a result from search links, canonical tags or hidden metadata.

Login, checkpoint, challenge, block, error, search-results and no-post states clear any prior result and return no address. A controlled fixture is available only behind an explicit environment gate and a literal-loopback HTTP root; it is prominently disclosed as not Facebook and accepts no real credential.

## Consequences

- User and agent operate the same viewport without copying an authenticated browser session.
- Only the user-selected current direct post can become a result; DCx does not crawl, rank or fabricate candidates.
- The accessible modeless window supports keyboard login handoff, search launch, explicit inspection, result focus and copy.
- Fixture success proves product state and accessibility wiring, not live Facebook stability, policy eligibility or human login/MFA acceptance.

## Alternatives rejected

- Copy Chrome or Edge cookies: this violates the authentication boundary and creates credential-exfiltration risk.
- Scrape search-result links automatically: results are page-controlled, unstable and not proof that the user selected the intended post.
- Trust canonical/Open Graph metadata: an untrusted page could point DCx at a different address.
