using System.Globalization;
using System.Net;
using System.Text;

namespace DCx.Core.Documents;

public sealed class AccessiblePdfHtmlBuilder
{
    private const int MaximumPages = 2_000;
    private readonly AccessibleDocumentValidator validator = new();

    public string BuildFlattenedLayout(AccessibleDocument document)
    {
        var validation = validator.Validate(document);
        if (!validation.IsValid) throw new InvalidOperationException(string.Join(" ", validation.Errors));
        if (document.Blocks.Any(block => block.Bounds is null))
            throw new InvalidOperationException("Every block requires reviewed page geometry for flattened PDF export.");

        var pages = document.Blocks
            .GroupBy(block => block.PageNumber)
            .OrderBy(page => page.Key)
            .ToArray();
        if (pages.Length == 0 || pages.Length > MaximumPages ||
            pages.Select((page, index) => page.Key == index + 1).Any(isSequential => !isSequential))
        {
            throw new InvalidOperationException("Flattened PDF pages must be sequential and within the page limit.");
        }

        var language = CultureInfo.GetCultureInfo(document.Language).Name;
        var html = new StringBuilder();
        html.Append("<!doctype html><html lang=\"").Append(Encode(language))
            .Append("\"><head><meta charset=\"utf-8\">")
            .Append("<meta http-equiv=\"Content-Security-Policy\" content=\"default-src 'none'; style-src 'unsafe-inline'; img-src data:\">")
            .Append("<title>").Append(Encode(document.Title)).Append("</title>")
            .Append("<style>@page{size:8.5in 11in;margin:0}*{box-sizing:border-box}html,body{margin:0;padding:0;background:#fff;color:#000;font-family:Arial,sans-serif}")
            .Append("main{margin:0;padding:0}.page{position:relative;width:8.5in;height:11in;overflow:hidden;break-after:page;page-break-after:always}")
            .Append(".page:last-child{break-after:auto;page-break-after:auto}.block{position:absolute;margin:0;white-space:pre-wrap;overflow:hidden;font-size:10pt;line-height:1.15}")
            .Append(".field-label{font-weight:700}.sr-only{position:absolute!important;width:1px;height:1px;padding:0;margin:-1px;overflow:hidden;clip:rect(0,0,0,0);white-space:nowrap;border:0}</style>")
            .Append("</head><body><main aria-label=\"").Append(Encode(document.Title)).Append("\">");

        foreach (var page in pages)
        {
            html.Append("<section class=\"page\" aria-label=\"Page ").Append(page.Key)
                .Append("\"><h1 class=\"sr-only\">").Append(Encode(document.Title))
                .Append(", page ").Append(page.Key).Append("</h1>");
            foreach (var block in page.OrderBy(block => block.ReadingOrder))
            {
                AppendBlock(html, block);
            }

            html.Append("</section>");
        }

        return html.Append("</main></body></html>").ToString();
    }

    private static void AppendBlock(StringBuilder html, DocumentBlock block)
    {
        var bounds = block.Bounds!;
        var style = FormattableString.Invariant(
            $"left:{bounds.X * 100:0.####}%;top:{bounds.Y * 100:0.####}%;width:{bounds.Width * 100:0.####}%;height:{bounds.Height * 100:0.####}%");
        var tag = block.Role == DocumentBlockRole.Heading ? "h2" : "div";
        html.Append('<').Append(tag).Append(" class=\"block\" style=\"").Append(style).Append("\"");
        if (block.Role == DocumentBlockRole.ImageDescription)
        {
            html.Append(" role=\"img\" aria-label=\"")
                .Append(Encode(block.AccessibleLabel ?? block.Text)).Append("\"");
        }
        else if (block.Role == DocumentBlockRole.Table)
        {
            html.Append(" role=\"table\" aria-label=\"")
                .Append(Encode(block.AccessibleLabel ?? "Table")).Append("\"");
        }

        html.Append('>');
        if (block.Role == DocumentBlockRole.FormField)
        {
            html.Append("<span class=\"field-label\">").Append(Encode(block.AccessibleLabel))
                .Append(": </span><span>").Append(Encode(block.Text)).Append("</span>");
        }
        else
        {
            html.Append(Encode(block.Text));
        }

        html.Append("</").Append(tag).Append('>');
    }

    private static string Encode(string? value) => WebUtility.HtmlEncode(value ?? string.Empty);
}
