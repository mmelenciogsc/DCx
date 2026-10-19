using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DCx.Core.Documents;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace DCx.Desktop.Documents;

public sealed class WebView2AccessiblePdfExporter : IDisposable
{
    private const int MaximumCdpResponseCharacters = 128 * 1024 * 1024;
    private const long MaximumPdfBytes = 96L * 1024 * 1024;
    private readonly WebView2 browser;
    private readonly string workingRoot;
    private readonly AccessiblePdfHtmlBuilder htmlBuilder = new();
    private bool initialized;
    private bool disposed;

    public WebView2AccessiblePdfExporter(WebView2 browser)
    {
        this.browser = browser ?? throw new ArgumentNullException(nameof(browser));
        workingRoot = Path.Combine(Path.GetTempPath(), $"DCx-pdf-export-{Guid.NewGuid():N}");
    }

    public async Task ExportFlattenedAsync(
        AccessibleDocument document,
        string outputPath,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        var canonical = Path.GetFullPath(outputPath);
        if (!string.Equals(Path.GetExtension(canonical), ".pdf", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The flattened accessible export must use a .pdf destination.");
        var destinationDirectory = Path.GetDirectoryName(canonical)
                                   ?? throw new InvalidOperationException("The PDF destination has no parent directory.");
        Directory.CreateDirectory(destinationDirectory);
        Directory.CreateDirectory(workingRoot);
        var htmlPath = Path.Combine(workingRoot, $"layout-{Guid.NewGuid():N}.html");
        var temporaryPdf = Path.Combine(
            destinationDirectory,
            $".{Path.GetFileNameWithoutExtension(canonical)}.{Guid.NewGuid():N}.tmp.pdf");
        try
        {
            var html = htmlBuilder.BuildFlattenedLayout(document);
            await File.WriteAllTextAsync(htmlPath, html, new UTF8Encoding(false), cancellationToken)
                .ConfigureAwait(true);
            await EnsureInitializedAsync(cancellationToken).ConfigureAwait(true);
            await NavigateToLocalLayoutAsync(htmlPath, cancellationToken).ConfigureAwait(true);
            cancellationToken.ThrowIfCancellationRequested();

            var result = await browser.CoreWebView2.CallDevToolsProtocolMethodAsync(
                "Page.printToPDF",
                "{\"landscape\":false,\"displayHeaderFooter\":false,\"printBackground\":true," +
                "\"scale\":1,\"paperWidth\":8.5,\"paperHeight\":11,\"marginTop\":0," +
                "\"marginBottom\":0,\"marginLeft\":0,\"marginRight\":0," +
                "\"preferCSSPageSize\":true,\"generateTaggedPDF\":true," +
                "\"generateDocumentOutline\":true}").ConfigureAwait(true);
            cancellationToken.ThrowIfCancellationRequested();
            await WriteStrictTaggedPdfAsync(result, temporaryPdf, cancellationToken).ConfigureAwait(true);
            if (!IsCompleteTaggedPdf(temporaryPdf))
                throw new InvalidOperationException("WebView2 did not produce a complete tagged PDF.");
            File.Move(temporaryPdf, canonical, overwrite: true);
        }
        finally
        {
            DeleteFileBestEffort(htmlPath);
            DeleteFileBestEffort(temporaryPdf);
        }
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        browser.Dispose();
        try
        {
            if (Directory.Exists(workingRoot)) Directory.Delete(workingRoot, recursive: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // The random temporary profile contains only the local export surface.
        }
    }

    private async Task EnsureInitializedAsync(CancellationToken cancellationToken)
    {
        if (initialized) return;
        cancellationToken.ThrowIfCancellationRequested();
        var profileRoot = Path.Combine(workingRoot, "webview2-profile");
        Directory.CreateDirectory(profileRoot);
        var environment = await CoreWebView2Environment.CreateAsync(
            browserExecutableFolder: null,
            userDataFolder: profileRoot,
            options: InstalledWebView2RuntimeProbe.CreateStableOptions()).ConfigureAwait(true);
        InstalledWebView2RuntimeProbe.RequireStableVersion(environment.BrowserVersionString);
        await browser.EnsureCoreWebView2Async(environment).ConfigureAwait(true);
        browser.CoreWebView2.Settings.AreDevToolsEnabled = false;
        browser.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
        browser.CoreWebView2.Settings.IsScriptEnabled = false;
        browser.CoreWebView2.Settings.IsWebMessageEnabled = false;
        browser.CoreWebView2.Settings.IsPasswordAutosaveEnabled = false;
        browser.CoreWebView2.Settings.IsGeneralAutofillEnabled = false;
        browser.CoreWebView2.NavigationStarting += OnNavigationStarting;
        initialized = true;
    }

    private async Task NavigateToLocalLayoutAsync(string htmlPath, CancellationToken cancellationToken)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs args)
        {
            if (args.IsSuccess) completion.TrySetResult();
            else completion.TrySetException(new InvalidOperationException("The local PDF layout did not load."));
        }

        browser.CoreWebView2.NavigationCompleted += OnCompleted;
        try
        {
            browser.CoreWebView2.Navigate(new Uri(htmlPath).AbsoluteUri);
            await completion.Task.WaitAsync(cancellationToken).ConfigureAwait(true);
        }
        finally
        {
            browser.CoreWebView2.NavigationCompleted -= OnCompleted;
        }
    }

    private void OnNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs args)
    {
        if (!Uri.TryCreate(args.Uri, UriKind.Absolute, out var uri) || !uri.IsFile ||
            !Path.GetFullPath(uri.LocalPath).StartsWith(
                Path.GetFullPath(workingRoot) + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase))
        {
            args.Cancel = true;
        }
    }

    private static async Task WriteStrictTaggedPdfAsync(
        string responseJson,
        string path,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(responseJson) || responseJson.Length > MaximumCdpResponseCharacters)
            throw new InvalidOperationException("The tagged-PDF response exceeded its bound.");
        using var response = JsonDocument.Parse(responseJson, new JsonDocumentOptions
        {
            AllowTrailingCommas = false,
            CommentHandling = JsonCommentHandling.Disallow,
            MaxDepth = 4,
        });
        if (response.RootElement.ValueKind != JsonValueKind.Object ||
            response.RootElement.EnumerateObject().Count() != 1 ||
            !response.RootElement.TryGetProperty("data", out var data) ||
            data.ValueKind != JsonValueKind.String)
        {
            throw new InvalidOperationException("The tagged-PDF response contract changed.");
        }

        var encoded = data.GetString();
        if (string.IsNullOrEmpty(encoded) || encoded.Length > MaximumCdpResponseCharacters)
            throw new InvalidOperationException("The tagged-PDF data was absent or excessive.");
        byte[]? bytes = null;
        try
        {
            bytes = Convert.FromBase64String(encoded);
            if (bytes.LongLength > MaximumPdfBytes)
                throw new InvalidOperationException("The tagged PDF exceeded its output bound.");
            await using var output = new FileStream(
                path,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                64 * 1024,
                FileOptions.Asynchronous | FileOptions.WriteThrough);
            await output.WriteAsync(bytes, cancellationToken).ConfigureAwait(true);
            await output.FlushAsync(cancellationToken).ConfigureAwait(true);
        }
        catch (FormatException exception)
        {
            throw new InvalidOperationException("The tagged-PDF response was not valid base64.", exception);
        }
        finally
        {
            if (bytes is not null) CryptographicOperations.ZeroMemory(bytes);
        }
    }

    private static bool IsCompleteTaggedPdf(string path)
    {
        var file = new FileInfo(path);
        if (!file.Exists || file.Length < 128 || file.Length > MaximumPdfBytes) return false;
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        Span<byte> header = stackalloc byte[5];
        if (stream.Read(header) != header.Length || !header.SequenceEqual("%PDF-"u8)) return false;
        var tailLength = (int)Math.Min(2048, stream.Length);
        stream.Position = stream.Length - tailLength;
        var tail = new byte[tailLength];
        stream.ReadExactly(tail);
        if (!Encoding.ASCII.GetString(tail).Contains("%%EOF", StringComparison.Ordinal)) return false;
        stream.Position = 0;
        var bytes = new byte[(int)stream.Length];
        stream.ReadExactly(bytes);
        return ContainsAscii(bytes, "/StructTreeRoot") && ContainsAscii(bytes, "/MarkInfo");
    }

    private static bool ContainsAscii(byte[] bytes, string expected) =>
        bytes.AsSpan().IndexOf(Encoding.ASCII.GetBytes(expected)) >= 0;

    private static void DeleteFileBestEffort(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Preserve the original export result.
        }
    }
}
