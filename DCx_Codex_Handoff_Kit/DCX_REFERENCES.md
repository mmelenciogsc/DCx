# Official reference URLs — verify version, licensing and availability at execution time

Updated/checkpointed: 2026-10-17. The Codex agent must open the URLs, capture date/version/actual relevant terms, and avoid treating a catalog listing as proof of account entitlement.

## OpenAI / Codex
- https://developers.openai.com/codex/ — Codex official documentation hub; check CLI, app-server, SDK and configuration.
- https://developers.openai.com/siwc/quickstart — Sign in with ChatGPT integration types; **selected commercial partners**; do not assume proprietary DCx approved.
- https://developers.openai.com/siwc/token-sharing-open-source — eligible open-source client ChatGPT plan usage; commercial proprietary applicability must be verified, not assumed.
- https://developers.openai.com/siwc/token-sharing-open-source/sign-in — detailed open-source registration, PKCE and token guidance, only when relevant/eligible.
- https://developers.openai.com/siwc/token-sharing-open-source/codex-app-server — app-server OAuth integration, structured invocation and limitations.
- https://developers.openai.com/siwc/token-sharing-open-source/preview-limitations — feature and model limitations; account plan != unrestricted API entitlement.
- https://platform.openai.com/docs/ — OpenAI API reference/Responses and models.
- https://platform.openai.com/pricing — prices and usage; capture effective rates dynamically.

## Windows / desktop / browser / accessibility
- https://learn.microsoft.com/en-us/windows/apps/develop/ui/controls/webview2 — native Windows UI / WebView2, sessions and OAuth caution.
- https://learn.microsoft.com/en-us/microsoft-edge/webview2/landing/ — runtime, deployment, profiles, samples and development.
- https://learn.microsoft.com/en-us/microsoft-edge/webview2/concepts/overview-features-apis — actual supported API behavior.
- https://learn.microsoft.com/en-us/windows/apps/winui/winui3/ — WinUI 3.
- https://learn.microsoft.com/en-us/dotnet/desktop/wpf/ — WPF alternative and tooling.
- https://learn.microsoft.com/en-us/windows/win32/winauto/entry-uiauto-win32 — Microsoft UI Automation.
- https://learn.microsoft.com/en-us/windows/dev-environment/wsl-interop — Windows/WSL subprocess and filesystem interoperability.
- https://learn.microsoft.com/en-us/windows/wsl/interop — prefer native Windows filesystem for Windows builds; WSL filesystem for Linux builds.
- https://learn.microsoft.com/en-us/windows/msix/ — MSIX deployment/signing.
- https://www.w3.org/TR/wai-aria-1.2/ — WAI-ARIA semantics.
- https://www.w3.org/WAI/WCAG22/quickref/ — WCAG 2.2 quick reference.

## Local inference / media / formats
- https://docs.ollama.com/ — actual local install, supported backends, quantized model APIs, vision and embeddings.
- https://github.com/ggml-org/whisper.cpp — offline speech inference; licenses and build instructions.
- https://ffmpeg.org/documentation.html — media pipelines.
- https://mozilla.github.io/pdf.js/ — PDF viewing.
- https://github.com/ClosedXML/ClosedXML — spreadsheet XLSX authoring, check license/version.
- https://www.nuget.org/ — managed .NET package provenance and pinned package versions.

## CKB, CCB and sync
- https://qdrant.tech/documentation/ — vector DB; choose local and server strategies deliberately.
- https://qdrant.tech/documentation/operations/snapshots/ — snapshot mechanics: snapshots are not automatically an incremental client synchronization protocol.
- https://www.sqlite.org/docs.html — local durable transactional metadata DB.
- https://docs.docker.com/compose/ — local Ubuntu server simulation before deployment.
- https://docs.sigstore.dev/ — artifact signature/provenance options.
- https://owasp.org/www-project-top-10-for-large-language-model-applications/ — prompt injection and tool-risk guidance.

## PayPal / accounting
- https://developer.paypal.com/subscriptions/about/ — recurring subscriptions; verify current account availability and region.
- https://developer.paypal.com/platforms/subscriptions/ — alternative subscription platform docs.
- https://developer.paypal.com/docs/api/subscriptions/v1/ — REST plans and subscription lifecycle.
- https://developer.paypal.com/docs/api/webhooks/v1/ — webhook verification/lifecycle.
- https://developer.paypal.com/docs/payouts/ — payouts product, approval, regional availability and fees.
- https://developer.paypal.com/docs/payouts/standard/integrate-api/ — batch payouts, sandbox and idempotency.
- https://developer.paypal.com/docs/payouts/standard/reference/payment-processing/ — payout statuses, not all completed synchronously.
- https://developer.paypal.com/tools/sandbox/ — sandbox credentials and mock transactions.

## Strict blockers to highlight in build log
- Commercial 'Sign in with ChatGPT' and ChatGPT-plan inference must be permitted for actual DCx distribution; keep API-key mode functional independently.
- PayPal Payouts not assumed enabled until merchant account confirmation; sandbox first.
- Facebook automated search may not be technically stable or permitted by the site; support manual assisted browsing and never claim a found URL without inspection.
- QuickBooks live application credentials absent: use sandbox/test fixtures and user-authorized OAuth only.
- Licensing review for community-uploaded code/assets and distribution/payout regulations required before commercial launch.
