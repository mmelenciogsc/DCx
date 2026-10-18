using System.Text.Json;
using System.Text.Json.Serialization;
using DCx.Core.Social;
using Microsoft.Web.WebView2.Wpf;

namespace DCx.Desktop.Automation;

internal sealed class FacebookHistoricalPostPageInspector
{
    private const int MaximumResponseCharacters = 32_768;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    private const string InspectionScript = """
        (() => {
          const compact = (value, limit) => String(value || '').replace(/\s+/g, ' ').trim().slice(0, limit) || null;
          const article = document.querySelector('main [role="article"], main article, [role="main"] [role="article"], article');
          const heading = article?.querySelector('h1,h2,[role="heading"]');
          const time = article?.querySelector('time');
          const mediaLabels = article
            ? Array.from(article.querySelectorAll('img[alt],video[aria-label],[role="img"][aria-label]'))
                .map(element => compact(element.getAttribute('alt') || element.getAttribute('aria-label'), 300))
                .filter(Boolean)
                .slice(0, 8)
            : [];
          const bodyText = compact(document.body?.innerText, 6000) || '';
          const lowerBody = bodyText.toLowerCase();
          const fixtureBlocked = Boolean(document.querySelector('[data-dcx-social-blocked="true"]'));
          return {
            documentTitle: compact(document.title, 300),
            articleHeading: compact(heading?.innerText || heading?.textContent, 300),
            articleText: compact(article?.innerText || article?.textContent, 4000),
            publishedText: compact(time?.innerText || time?.textContent, 128),
            publishedDateTime: compact(time?.getAttribute('datetime'), 128),
            mediaLabels,
            hasArticle: Boolean(article),
            hasLoginPrompt: Boolean(document.querySelector('input[type="password"], form[action*="/login"]')),
            hasAutomationBlock: fixtureBlocked || lowerBody.includes('temporarily blocked') || lowerBody.includes('automated behavior'),
            hasErrorPage: Boolean(document.querySelector('[role="alert"][data-error-page="true"]'))
          };
        })()
        """;

    public async Task<FacebookPostPageEvidence> InspectAsync(
        WebView2 browser,
        Uri address,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(browser);
        ArgumentNullException.ThrowIfNull(address);
        if (browser.CoreWebView2 is null || browser.Source is null)
        {
            throw new InvalidOperationException("The shared browser page is not ready for inspection.");
        }

        var json = await browser.CoreWebView2.ExecuteScriptAsync(InspectionScript)
            .WaitAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(json) || json.Length > MaximumResponseCharacters)
        {
            throw new InvalidOperationException("The visible page inspection response was missing or too large.");
        }

        var raw = JsonSerializer.Deserialize<RawPageEvidence>(json, JsonOptions)
            ?? throw new InvalidOperationException("The visible page returned no inspection evidence.");
        if (raw.MediaLabels is null || raw.MediaLabels.Length > 8)
        {
            throw new InvalidOperationException("The visible page returned invalid media evidence.");
        }

        return new FacebookPostPageEvidence(
            address,
            raw.DocumentTitle,
            raw.ArticleHeading,
            raw.ArticleText,
            raw.PublishedText,
            raw.PublishedDateTime,
            raw.MediaLabels,
            raw.HasArticle,
            raw.HasLoginPrompt,
            raw.HasAutomationBlock,
            raw.HasErrorPage);
    }

    private sealed record RawPageEvidence(
        string? DocumentTitle,
        string? ArticleHeading,
        string? ArticleText,
        string? PublishedText,
        string? PublishedDateTime,
        string[]? MediaLabels,
        bool HasArticle,
        bool HasLoginPrompt,
        bool HasAutomationBlock,
        bool HasErrorPage);
}
