# YouTube and streamed-media limitations

DCx treats a YouTube URL as permission to navigate the shared DCx WebView2 browser to that page. It does not download, decrypt, copy, restream, or remove advertising from YouTube content.

- Sign-in, MFA, age or region checks, consent dialogs, purchases, and account approvals remain manual user actions in the shared browser viewport.
- Playback depends on YouTube, WebView2, network availability, the video's region/age/rights settings, and site changes. Live streams, DRM-protected media, private videos, deleted videos, and every codec are not guaranteed.
- DCx accepts only HTTPS `youtube.com` and `youtu.be` video URLs after exact-host and video-ID validation. It rejects credentials, lookalike hosts, and unsupported page types.
- The P07 automated gate proves normalized URL handoff into a DCx-owned WebView2 tab. It does not claim successful playback of every YouTube video or authenticated account state.
- Local video and a deterministic loopback HTML5-video fixture now support a bounded optional question, pause, local-only frame analysis, follow-up questions while paused and explicit resume. For web video, DCx selects only the largest ready, visible top-document HTML5 `video`, clips capture to its visible rectangle and sends that image only to the already approved digest-bound loopback vision model. It does not intentionally persist the frame, question or answer.
- A direct YouTube page may work only while its foreground player remains an accessible ordinary top-document HTML5 video and WebView2 permits rectangle capture. Cross-origin iframe players, live streams, DRM/protected surfaces, ads, sign-in/consent overlays and provider changes may block pause, capture or resume. The automated gate uses a generated loopback video and does not claim live or authenticated YouTube acceptance.
- DCx does not copy browser cookies from Chrome, Edge, or another browser, and does not bypass DRM, access controls, advertising, or provider terms.
- Audio transcription remains unavailable until the separately tracked P06 Whisper-backend gate is approved, installed, benchmarked, and passed.
