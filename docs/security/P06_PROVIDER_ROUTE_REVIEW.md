# P06 provider route review

Date: 2026-10-18

This is read-only route evidence, not download or production authority. The authenticated catalog and plan remain SHA-256 `1ea3bfb04567757e0207cfd824fb5284faad2552617cbbe3330a13f66079e325` and `a0d997c6298fdf5f592241928c6fd9564c6de0c3e023c9eb69bd1df96406c286`.

## Reproduction

Build `DCx.Core` Release first, then explicitly authorize only the public route audit:

```powershell
pwsh.exe -NoProfile -ExecutionPolicy Bypass -File D:\DCx\scripts\audit-p06-provider-routes.ps1 -ExplicitReadOnlyNetworkAudit
```

Omitting the switch fails before any network request. The audit uses the native Windows .NET handler with automatic redirects, proxy, cookies, credentials, preauthentication and decompression disabled. It sends only bounded `HEAD` requests and manually follows at most three safe redirects. It reads zero response bodies and stores no redirect query values.

## Measured observation

Evidence: `artifacts/test-results/p06-route-audit-89f5876ba34549cba66bf32d8941dcc5/provider-route-audit.json` (intentionally uncommitted; generated from clean exact source `b82c270636aedd521b5e2c7a44e6ce4207df9484`).

- Plan items: 9.
- HEAD requests: 12.
- Manual redirects: 3; automatic redirects: 0.
- Response bodies, proxy use, cookies, credentials and authorization headers: 0.
- Every final response returned HTTP 200.
- Eight final responses exposed the exact expected content length. The 708-byte Ollama manifest returned no HEAD content length, so payload length was not claimed as verified.
- The evidence serialization contains no `?` query delimiter. Signed query presence and character count are retained, never the query value.

Observed redirect destinations, without queries:

| Catalog item | First-hop destination | Final length |
|---|---|---:|
| whisper.cpp CUDA runtime | `https://release-assets.githubusercontent.com/github-production-release-asset/541269386/96f57729-d0dc-439f-85b9-56f6e8905bc6` | 284,761,231 |
| multilingual base model | `https://us.aws.cdn.hf.co/xet-bridge-us/641ab5d15d107c5c5f346372/2f62d18b50c3f3feafbf990eec23a93d319660b1efbdd3fff55e52b7cde2e374` | 147,951,465 |
| large-v3-turbo Q5 model | `https://us.aws.cdn.hf.co/xet-bridge-us/641ab5d15d107c5c5f346372/9c7b9c6bf60cf555f34fe7d81e8643764ff03d2f60b6fa550f5630be52eef830` | 574,041,195 |

The other six plan items were direct: two pinned license objects, the Ollama manifest, and three digest-addressed Ollama blobs.

## Decision and remaining limits

Production stays deny-all. GitHub and Hugging Face use signed query URLs; the Hugging Face host is region-specific and is not established as a stable distribution contract for every DCx user. The existing rule type binds exact item source/length/digest plus target origin/path prefix, but one time-and-location observation is insufficient grounds to compile a global production allowlist.

No GET, payload byte, digest verification, archive inspection, contained-license review, install, model pull, registration, selection or execution occurred. A first ad-hoc WSL HEAD attempt was discarded because its local AWK formatter failed; it followed no redirect and yielded no accepted evidence. Only the native Windows audit above is accepted.
