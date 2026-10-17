using DCx.Core.Documents;
using System.IO;
using Windows.Data.Pdf;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage;
using Windows.Storage.Streams;

namespace DCx.Desktop.Documents;

public sealed class WindowsOcrService
{
    public async Task<AccessibleDocument> RecognizePdfAsync(string path, CancellationToken cancellationToken = default)
    {
        var file = await StorageFile.GetFileFromPathAsync(Path.GetFullPath(path));
        var pdf = await PdfDocument.LoadFromFileAsync(file);
        var engine = OcrEngine.TryCreateFromUserProfileLanguages()
            ?? throw new InvalidOperationException("Windows OCR has no configured recognition language.");
        var blocks = new List<DocumentBlock>();
        for (uint pageIndex = 0; pageIndex < pdf.PageCount; pageIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var page = pdf.GetPage(pageIndex);
            using var stream = new InMemoryRandomAccessStream();
            await page.RenderToStreamAsync(stream);
            var decoder = await BitmapDecoder.CreateAsync(stream);
            using var bitmap = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
            var result = await engine.RecognizeAsync(bitmap);
            var order = 0;
            foreach (var line in result.Lines)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (line.Words.Count == 0 || string.IsNullOrWhiteSpace(line.Text)) continue;
                var left = line.Words.Min(word => word.BoundingRect.X);
                var top = line.Words.Min(word => word.BoundingRect.Y);
                var right = line.Words.Max(word => word.BoundingRect.X + word.BoundingRect.Width);
                var bottom = line.Words.Max(word => word.BoundingRect.Y + word.BoundingRect.Height);
                blocks.Add(new DocumentBlock(
                    $"page-{pageIndex + 1}-line-{order + 1}",
                    (int)pageIndex + 1,
                    order++,
                    order == 1 ? DocumentBlockRole.Heading : DocumentBlockRole.Paragraph,
                    line.Text.Trim(),
                    new NormalizedRectangle(left / bitmap.PixelWidth, top / bitmap.PixelHeight,
                        (right - left) / bitmap.PixelWidth, (bottom - top) / bitmap.PixelHeight)));
            }
        }

        return new AccessibleDocument(Path.GetFileNameWithoutExtension(path), blocks);
    }
}
