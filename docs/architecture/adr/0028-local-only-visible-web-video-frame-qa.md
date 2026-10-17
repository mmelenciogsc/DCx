# ADR 0028: local-only visible web-video frame Q&A

- Status: Accepted for deterministic fixture use; live-provider acceptance remains blocked
- Date: 2026-10-17

## Context

P07 could pause, extract and describe a local MP4, and could safely hand a normalized YouTube URL to the shared browser. P12 still required local/YouTube frame Q&A. Downloading provider media, copying browser credentials, bypassing access controls, or silently capturing the surrounding authenticated page would violate product boundaries. A provider-neutral mechanism must remain usable when the foreground page exposes an ordinary HTML5 video, while failing honestly for cross-origin iframes, live streams, DRM/protected surfaces, provider changes or unavailable local vision.

## Decision

Add one optional 500-character frame question shared by local and web video. Put a visual-evidence-only boundary ahead of the user question, treat text inside the frame as untrusted instructions, require concise direct answers, and require the model to say when the answer is not visually determinable.

For a user-invoked web request, operate only on the current DCx WebView2 tab. Select the largest ready, visible top-document HTML5 `video`, pause it, clip its visible bounds to the viewport, validate bounded finite geometry/time, and capture only that rectangle through the supported WebView2 DevTools protocol. Keep the image in a random private temporary directory under an exclusive-write then read-only/no-write/no-delete lease. Send the image and question only to the already user-approved, digest/license-bound loopback Ollama vision model. Zero owned image arrays, remove the temporary directory, keep playback paused for follow-up questions, and expose a separate explicit resume action bound to the same tab and page.

Never download or restream provider media, copy cookies from another browser, access hidden/cross-origin frames, bypass DRM/advertising/login/MFA/region/age controls, or claim universal YouTube support. Page changes invalidate the operation. Bound response/image/geometry/time values and return stable, content-free failure states.

## Consequences

- The deterministic loopback HTML5-video fixture now proves accessible pause, visible-rectangle question/answer, continued pause and explicit resume in the same shared browser viewport.
- Local MP4 description also accepts the same bounded optional question and continues to require explicit resume.
- A direct YouTube page may work only when its current top-level player remains an accessible ordinary HTML5 video and capture is permitted. Authenticated/live/protected/embedded/provider-specific acceptance remains unverified and blocked.
- Immutable JSON/base64/model-request strings cannot be securely erased; owned decoded image arrays are zeroed and no frame, prompt or answer is intentionally persisted by this boundary.

## Verified upstream contracts

- Microsoft WebView2 `ExecuteScriptAsync`: <https://learn.microsoft.com/en-us/dotnet/api/microsoft.web.webview2.core.corewebview2.executescriptasync>
- Microsoft WebView2 `CallDevToolsProtocolMethodAsync`: <https://learn.microsoft.com/en-us/dotnet/api/microsoft.web.webview2.core.corewebview2.calldevtoolsprotocolmethodasync>
- YouTube IFrame Player API reference and documented playback controls/limitations: <https://developers.google.com/youtube/iframe_api_reference>
- YouTube API Services Terms of Service: <https://developers.google.com/youtube/terms/api-services-terms-of-service>
