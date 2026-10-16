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
| OpenAI current-model resolver | Official OpenAI developer-doc resolver | Returned `gpt-6-astra` and exact migration/prompting guide URLs. No model is hardcoded into DCx; P04 must fetch the exact official guides and verify auth/distribution eligibility before integration. |

The remaining supplied official references are verified immediately before their associated integration phase. An HTTP success alone never proves account entitlement, commercial authorization, licensing suitability, or API availability.

