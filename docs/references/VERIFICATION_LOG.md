# Official-reference verification log

Checked 2026-10-17 from the development host.

| Topic | Official URL | Observed result |
|---|---|---|
| WPF overview | https://learn.microsoft.com/en-us/dotnet/desktop/wpf/overview/ | HTTP 200; Microsoft describes WPF as a Windows-only UI framework for .NET; page metadata updated 2026-03-30. |
| WinUI 3 | https://learn.microsoft.com/en-us/windows/apps/winui/winui3/ | HTTP 200; Microsoft describes WinUI 3 as Windows App SDK UI; page metadata updated 2026-10-02. |
| WebView2 | https://learn.microsoft.com/en-us/microsoft-edge/webview2/ | HTTP 200; Microsoft documents hosting web content in .NET/Win32 apps; page metadata updated 2026-10-02. |
| UI Automation | https://learn.microsoft.com/en-us/windows/win32/winauto/entry-uiauto-win32 | HTTP 200. |
| WSL interop/reference correction | https://learn.microsoft.com/en-us/windows/wsl/interop | HTTP 200; the `windows/dev-environment/wsl/interop` URL in the supplied reference file returned 404. |
| NuGet WebView2 versions | https://api.nuget.org/v3-flatcontainer/microsoft.web.webview2/index.json | Latest stable observed: 1.0.4258.31; pinned. |
| OpenAI current-model resolver | Official OpenAI developer-doc resolver | Returned `gpt-6-astra` and exact migration/prompting guide URLs. No model is hardcoded into DCx; runtime choices come from the authenticated catalog. |
| OpenAI API authentication | https://developers.openai.com/api/reference/overview | HTTP 200; bearer credentials, API-key secrecy, and API organization/project billing boundaries confirmed. |
| Codex authentication | https://developers.openai.com/codex/auth | HTTP 200; API-key and ChatGPT sign-in are distinct; API-key usage has standard Platform billing; `CODEX_HOME`, keyring/ephemeral credential storage and forced login method documented. |
| Codex app-server | https://developers.openai.com/codex/app-server | HTTP 200; JSONL stdio handshake, structured methods/events, model catalog caveat and interrupt lifecycle confirmed. Local `codex-cli 0.146.0` schemas were generated and inspected. |
| Sign in with ChatGPT | https://developers.openai.com/siwc/quickstart | HTTP 200; selected commercial partners/limited trial. Account sign-in does not grant conversations or unrestricted API access. |
| Open-source ChatGPT-plan sharing | https://developers.openai.com/siwc/token-sharing-open-source | HTTP 200; available path is for open-source/local clients; paid or remotely hosted apps are directed to an interest form. This is not evidence that DCx is approved. |
| ChatGPT plan via app-server | https://developers.openai.com/siwc/token-sharing-open-source/codex-app-server | HTTP 200; external OAuth access token, Responses provider configuration, initialize/thread/turn sequence and restart-on-token-renewal confirmed for eligible clients only. |
| ChatGPT-plan preview limitations | https://developers.openai.com/siwc/token-sharing-open-source/preview-limitations | HTTP 200; `store:false`, `stream:true`, explicit input history and unsupported fields/tools recorded. |
| Ollama local API | https://docs.ollama.com/api/introduction | HTTP 200; local base URL `http://localhost:11434/api` requires no cloud key. |
| Ollama vision | https://docs.ollama.com/capabilities/vision | HTTP 200; REST image input is base64 in a message `images` array. |
| Ollama chat schema | https://docs.ollama.com/api/chat | HTTP 200; request/response fields, deterministic options, token/duration metrics and `keep_alive` verified. |
| whisper.cpp | https://github.com/ggml-org/whisper.cpp | HTTP 200; upstream project exists, but no local Windows backend is installed and no binary/model license/hash has been approved. |
| FFmpeg documentation | https://ffmpeg.org/documentation.html | Timed out from this environment on the verification attempt; the installed binary/version and enabled backends were inspected locally instead. |

The remaining supplied official references are verified immediately before their associated integration phase. An HTTP success alone never proves account entitlement, commercial authorization, licensing suitability, or API availability.
