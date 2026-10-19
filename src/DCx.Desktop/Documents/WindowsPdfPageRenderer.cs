using System.IO;
using System.Security.Cryptography;
using Windows.Data.Pdf;
using Windows.Storage;

namespace DCx.Desktop.Documents;

internal sealed class WindowsPdfPageRenderer
{
    private const int MaximumPages = 2_000;
    private const uint MaximumDimensionPixels = 2_550;
    private const long MaximumPageBytes = 24L * 1024 * 1024;
    private const long MaximumTotalBytes = 80L * 1024 * 1024;

    public async Task<IReadOnlyList<RenderedPdfPage>> RenderAsync(
        string pdfPath,
        string outputDirectory,
        int expectedPageCount,
        string expectedSourceSha256,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pdfPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        if (expectedPageCount is < 1 or > MaximumPages)
            throw new InvalidOperationException("The reviewed PDF page count is outside the render limit.");
        if (string.IsNullOrWhiteSpace(expectedSourceSha256) || expectedSourceSha256.Length != 64 ||
            expectedSourceSha256.Any(character =>
                character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')))
            throw new InvalidOperationException("The reviewed PDF source digest is invalid.");

        var canonicalSource = Path.GetFullPath(pdfPath);
        var canonicalOutput = Path.GetFullPath(outputDirectory);
        Directory.CreateDirectory(canonicalOutput);
        if ((File.GetAttributes(canonicalOutput) & System.IO.FileAttributes.ReparsePoint) != 0)
            throw new InvalidOperationException("The PDF render directory cannot be a reparse point.");

        await using var source = new FileStream(
            canonicalSource,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read | FileShare.Delete,
            64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var expectedDigestBytes = Convert.FromHexString(expectedSourceSha256);
        var digestBytes = await SHA256.HashDataAsync(source, cancellationToken);
        try
        {
            if (!CryptographicOperations.FixedTimeEquals(expectedDigestBytes, digestBytes))
                throw new InvalidOperationException("The source PDF changed after OCR review.");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(expectedDigestBytes);
            CryptographicOperations.ZeroMemory(digestBytes);
        }
        source.Position = 0;
        using var randomAccessSource = source.AsRandomAccessStream();
        var pdf = await PdfDocument.LoadFromStreamAsync(randomAccessSource);
        if (pdf.PageCount != expectedPageCount)
            throw new InvalidOperationException("The source PDF page count no longer matches the reviewed document.");

        var folder = await StorageFolder.GetFolderFromPathAsync(canonicalOutput);
        var pages = new List<RenderedPdfPage>(expectedPageCount);
        var createdNames = new List<string>(expectedPageCount);
        long totalBytes = 0;
        try
        {
            for (uint pageIndex = 0; pageIndex < pdf.PageCount; pageIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                using var page = pdf.GetPage(pageIndex);
                var media = page.Dimensions.MediaBox;
                if (media.Width <= 0 || media.Height <= 0)
                    throw new InvalidOperationException("The source PDF contains invalid page geometry.");
                var scale = Math.Min(
                    MaximumDimensionPixels / media.Width,
                    MaximumDimensionPixels / media.Height);
                var width = checked((uint)Math.Max(1, Math.Round(media.Width * scale)));
                var height = checked((uint)Math.Max(1, Math.Round(media.Height * scale)));
                var name = $"page-{pageIndex + 1:D4}.png";
                var target = await folder.CreateFileAsync(name, CreationCollisionOption.ReplaceExisting);
                createdNames.Add(name);
                using (var stream = await target.OpenAsync(FileAccessMode.ReadWrite))
                {
                    var options = new PdfPageRenderOptions
                    {
                        DestinationWidth = width,
                        DestinationHeight = height,
                    };
                    await page.RenderToStreamAsync(stream, options);
                    await stream.FlushAsync();
                    if (stream.Size is < 64 or > MaximumPageBytes)
                        throw new InvalidOperationException("A rendered PDF page is outside the image-size limit.");
                    totalBytes = checked(totalBytes + (long)stream.Size);
                    if (totalBytes > MaximumTotalBytes)
                        throw new InvalidOperationException("Rendered PDF pages exceed the aggregate image-size limit.");
                }

                pages.Add(new RenderedPdfPage(name, media.Width, media.Height));
            }
        }
        catch
        {
            foreach (var name in createdNames)
            {
                try
                {
                    File.Delete(Path.Combine(canonicalOutput, name));
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    // The exporter's private working root is also removed when the wizard closes.
                }
            }

            throw;
        }

        return pages;
    }
}

internal sealed record RenderedPdfPage(string FileName, double Width, double Height);
