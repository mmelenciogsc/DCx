using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using UglyToad.PdfPig;

const int MaximumImages = 4_000;
const int MaximumImageDimension = 10_000;
const int MaximumSingleDecodedPngBytes = 24 * 1024 * 1024;
const int MaximumDecodedPngBytes = 96 * 1024 * 1024;

if (args.Length is < 2 or > 3)
{
    Console.Error.WriteLine("Usage: DCx.PdfInspector <input-pdf> <output-json> [decoded-image-directory]");
    return 2;
}

var input = Path.GetFullPath(args[0]);
var output = Path.GetFullPath(args[1]);
var decodedImageRoot = args.Length == 3 ? Path.GetFullPath(args[2]) : null;
var inputInfo = new FileInfo(input);
if (!inputInfo.Exists || inputInfo.Length is < 128 or > 96L * 1024 * 1024)
{
    Console.Error.WriteLine("The PDF is absent or outside the inspector bound.");
    return 3;
}
if (decodedImageRoot is not null &&
    (Path.GetPathRoot(decodedImageRoot)?.TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar) ==
        decodedImageRoot.TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar) ||
     Directory.Exists(decodedImageRoot) || File.Exists(decodedImageRoot)))
{
    Console.Error.WriteLine("The decoded-image destination is unsafe or already exists.");
    return 3;
}

var temporaryImageRoot = decodedImageRoot is null
    ? null
    : $"{decodedImageRoot}.{Guid.NewGuid():N}.tmp";
try
{
    if (temporaryImageRoot is not null)
    {
        Directory.CreateDirectory(temporaryImageRoot);
        if ((File.GetAttributes(temporaryImageRoot) & FileAttributes.ReparsePoint) != 0)
        {
            Console.Error.WriteLine("The decoded-image temporary destination is unsafe.");
            return 3;
        }
    }

    using var document = PdfDocument.Open(input);
    if (document.NumberOfPages is < 1 or > 2_000)
    {
        Console.Error.WriteLine("The PDF page count is outside the inspector bound.");
        return 4;
    }

    var pages = new List<object>(document.NumberOfPages);
    var totalWords = 0;
    var totalImages = 0;
    var totalDecodedPngBytes = 0;
    for (var number = 1; number <= document.NumberOfPages; number++)
    {
        var page = document.GetPage(number);
        if (page.Width <= 0 || page.Height <= 0)
        {
            Console.Error.WriteLine("The PDF contains invalid page geometry.");
            return 5;
        }

        var words = page.GetWords().ToArray();
        var images = page.GetImages().ToArray();
        totalWords = checked(totalWords + words.Length);
        totalImages = checked(totalImages + images.Length);
        if (totalWords > 50_000 || totalImages > MaximumImages)
        {
            Console.Error.WriteLine("The PDF exceeds an inspection bound.");
            return 6;
        }

        var inspectedImages = new List<object>(images.Length);
        for (var index = 0; index < images.Length; index++)
        {
            var image = images[index];
            if (image.WidthInSamples is < 1 or > MaximumImageDimension ||
                image.HeightInSamples is < 1 or > MaximumImageDimension)
            {
                Console.Error.WriteLine("The PDF contains an image outside the inspection bound.");
                return 6;
            }

            string? decodedPngFile = null;
            string? decodedPngSha256 = null;
            var decodedPngBytes = 0;
            if (image.TryGetPng(out var png))
            {
                try
                {
                    decodedPngBytes = png.Length;
                    totalDecodedPngBytes = checked(totalDecodedPngBytes + decodedPngBytes);
                    if (decodedPngBytes is < 64 or > MaximumSingleDecodedPngBytes ||
                        totalDecodedPngBytes > MaximumDecodedPngBytes)
                    {
                        Console.Error.WriteLine("The PDF contains decoded image data outside the inspection bound.");
                        return 6;
                    }

                    decodedPngSha256 = Convert.ToHexString(SHA256.HashData(png)).ToLowerInvariant();
                    if (temporaryImageRoot is not null)
                    {
                        decodedPngFile = $"page-{number:D4}-image-{index + 1:D4}.png";
                        await File.WriteAllBytesAsync(
                            Path.Combine(temporaryImageRoot, decodedPngFile),
                            png);
                    }
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(png);
                }
            }

            inspectedImages.Add(new
            {
                width_samples = image.WidthInSamples,
                height_samples = image.HeightInSamples,
                left_normalized = image.BoundingBox.Left / page.Width,
                top_normalized = 1 - (image.BoundingBox.Top / page.Height),
                right_normalized = image.BoundingBox.Right / page.Width,
                bottom_normalized = 1 - (image.BoundingBox.Bottom / page.Height),
                decoded_png_available = decodedPngSha256 is not null,
                decoded_png_bytes = decodedPngBytes,
                decoded_png_sha256 = decodedPngSha256,
                decoded_png_file = decodedPngFile,
            });
        }

        pages.Add(new
        {
            page_number = number,
            width = page.Width,
            height = page.Height,
            text = string.Concat(page.Letters.Select(letter => letter.Value)),
            image_count = images.Length,
            images = inspectedImages,
            words = words.Select(word => new
            {
                text = word.Text,
                left_normalized = word.BoundingBox.Left / page.Width,
                top_normalized = 1 - (word.BoundingBox.Top / page.Height),
                right_normalized = word.BoundingBox.Right / page.Width,
                bottom_normalized = 1 - (word.BoundingBox.Bottom / page.Height),
            }).ToArray(),
        });
    }

    Directory.CreateDirectory(Path.GetDirectoryName(output)!);
    var temporary = $"{output}.{Guid.NewGuid():N}.tmp";
    try
    {
        var json = JsonSerializer.Serialize(new
        {
            page_count = document.NumberOfPages,
            word_count = totalWords,
            image_count = totalImages,
            decoded_png_bytes = totalDecodedPngBytes,
            pages,
        }, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(temporary, json);
        if (temporaryImageRoot is not null)
        {
            Directory.Move(temporaryImageRoot, decodedImageRoot!);
        }
        File.Move(temporary, output, overwrite: true);
    }
    finally
    {
        if (File.Exists(temporary)) File.Delete(temporary);
    }

    return 0;
}
finally
{
    DeleteGeneratedImageDirectoryBestEffort(temporaryImageRoot);
}

static void DeleteGeneratedImageDirectoryBestEffort(string? path)
{
    if (path is null || !Directory.Exists(path)) return;

    try
    {
        var directory = new DirectoryInfo(path);
        if ((directory.Attributes & FileAttributes.ReparsePoint) != 0 ||
            directory.EnumerateDirectories().Any())
        {
            return;
        }

        var files = directory.EnumerateFiles().ToArray();
        if (files.Any(file =>
                (file.Attributes & FileAttributes.ReparsePoint) != 0 ||
                !Regex.IsMatch(
                    file.Name,
                    "^page-[0-9]{4}-image-[0-9]{4}\\.png$",
                    RegexOptions.CultureInvariant)))
        {
            return;
        }

        foreach (var file in files) file.Delete();
        directory.Delete();
    }
    catch (IOException)
    {
        // Cleanup is best effort; never broaden deletion after a filesystem race.
    }
    catch (UnauthorizedAccessException)
    {
        // Cleanup is best effort; the validation caller can remove retained artifacts.
    }
}
