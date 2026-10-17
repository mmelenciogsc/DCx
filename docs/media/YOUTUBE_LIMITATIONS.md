# YouTube and streamed-media limitations

DCx treats a YouTube URL as permission to navigate the shared DCx WebView2 browser to that page. It does not download, decrypt, copy, restream, or remove advertising from YouTube content.

- Sign-in, MFA, age or region checks, consent dialogs, purchases, and account approvals remain manual user actions in the shared browser viewport.
- Playback depends on YouTube, WebView2, network availability, the video's region/age/rights settings, and site changes. Live streams, DRM-protected media, private videos, deleted videos, and every codec are not guaranteed.
- DCx accepts only HTTPS `youtube.com` and `youtu.be` video URLs after exact-host and video-ID validation. It rejects credentials, lookalike hosts, and unsupported page types.
- The P07 automated gate proves normalized URL handoff into a DCx-owned WebView2 tab. It does not claim successful playback of every YouTube video or authenticated account state.
- Frame extraction and local-VLM description are currently implemented only for admitted local video files. YouTube frame Q&A requires a separately permissioned, policy-compliant capture path and is not claimed by P07.
- DCx does not copy browser cookies from Chrome, Edge, or another browser, and does not bypass DRM, access controls, advertising, or provider terms.
- Audio transcription remains unavailable until the separately tracked P06 Whisper-backend gate is approved, installed, benchmarked, and passed.
