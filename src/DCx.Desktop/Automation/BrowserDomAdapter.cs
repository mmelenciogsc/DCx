using System.Text.Json;
using DCx.Core.Automation;
using DCx.Core.Control;
using Microsoft.Web.WebView2.Wpf;

namespace DCx.Desktop.Automation;

public sealed class BrowserDomAdapter(BrowserControlCoordinator controlCoordinator)
{
    private const int MaximumElementIdLength = 256;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
    };

    private const string SnapshotScript = """
        (() => {
          const nameOf = (element) => {
            const labelledBy = element.getAttribute('aria-labelledby');
            if (labelledBy) {
              const text = labelledBy.split(/\s+/).map(id => document.getElementById(id)?.innerText || '').join(' ').trim();
              if (text) return text;
            }
            const aria = element.getAttribute('aria-label');
            if (aria) return aria.trim();
            if (element.labels && element.labels.length) {
              const text = Array.from(element.labels).map(label => label.innerText || '').join(' ').trim();
              if (text) return text;
            }
            return (element.innerText || element.getAttribute('placeholder') || element.getAttribute('title') || '').trim();
          };
          return Array.from(document.querySelectorAll('a,button,input,select,textarea,[role],[aria-label]'))
            .slice(0, 250)
            .map(element => ({
              tag: element.tagName.toLowerCase(),
              id: element.id || null,
              role: element.getAttribute('role'),
              name: nameOf(element).slice(0, 500) || null,
              type: element.getAttribute('type'),
              disabled: Boolean(element.disabled || element.getAttribute('aria-disabled') === 'true')
            }));
        })()
        """;

    public async Task<IReadOnlyList<BrowserDomElementSnapshot>> ReadSnapshotAsync(
        WebView2 browser,
        AgentActionLease lease,
        Guid tabId,
        long navigationGeneration)
    {
        if (!TryAuthorize(browser, lease, tabId, navigationGeneration, out var rejectionReason))
        {
            throw new InvalidOperationException(rejectionReason);
        }

        var json = await browser.CoreWebView2.ExecuteScriptAsync(SnapshotScript);
        return JsonSerializer.Deserialize<List<BrowserDomElementSnapshot>>(json, JsonOptions) ?? [];
    }

    public async Task<BrowserDomActionResult> ExecuteAsync(
        WebView2 browser,
        AgentActionLease lease,
        Guid tabId,
        long navigationGeneration,
        BrowserDomAction action)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (!TryAuthorize(browser, lease, tabId, navigationGeneration, out var rejectionReason))
        {
            return new BrowserDomActionResult(false, null, rejectionReason);
        }

        if (string.IsNullOrWhiteSpace(action.ElementId) || action.ElementId.Length > MaximumElementIdLength)
        {
            return new BrowserDomActionResult(false, null, "The element identifier is missing or too long.");
        }

        var elementIdJson = JsonSerializer.Serialize(action.ElementId, JsonOptions);
        var actionJson = JsonSerializer.Serialize(action.Kind.ToString(), JsonOptions);
        var script = $$"""
            (() => {
              const elementId = {{elementIdJson}};
              const action = {{actionJson}};
              const element = document.getElementById(elementId);
              if (!element) return { succeeded: false, value: null, error: 'Element not found.' };
              if (element.disabled || element.getAttribute('aria-disabled') === 'true') {
                return { succeeded: false, value: null, error: 'Element is disabled.' };
              }
              if (action === 'FocusById') {
                element.focus({ preventScroll: false });
                return { succeeded: document.activeElement === element, value: null, error: null };
              }
              if (action === 'ClickById') {
                element.click();
                return { succeeded: true, value: null, error: null };
              }
              if (action === 'ReadValueById') {
                const type = (element.getAttribute('type') || '').toLowerCase();
                if (type === 'password') return { succeeded: false, value: null, error: 'Password values cannot be read.' };
                if (!('value' in element)) return { succeeded: false, value: null, error: 'Element has no readable value.' };
                return { succeeded: true, value: String(element.value).slice(0, 4096), error: null };
              }
              return { succeeded: false, value: null, error: 'Unsupported action.' };
            })()
            """;

        var json = await browser.CoreWebView2.ExecuteScriptAsync(script);
        return JsonSerializer.Deserialize<BrowserDomActionResult>(json, JsonOptions)
            ?? new BrowserDomActionResult(false, null, "The page returned no action result.");
    }

    private bool TryAuthorize(
        WebView2 browser,
        AgentActionLease lease,
        Guid tabId,
        long navigationGeneration,
        out string rejectionReason)
    {
        if (browser.CoreWebView2 is null || browser.Source is null)
        {
            rejectionReason = "The browser page is not ready.";
            return false;
        }

        return controlCoordinator.IsDomActionAllowed(
            lease.LeaseId,
            tabId,
            browser.Source,
            navigationGeneration,
            out rejectionReason);
    }
}

