using System.Text.Json;
using UglyToad.PdfPig;

if (args.Length != 2)
{
    Console.Error.WriteLine("Usage: DCx.PdfInspector <input-pdf> <output-json>");
    return 2;
}

var input = Path.GetFullPath(args[0]);
var output = Path.GetFullPath(args[1]);
var inputInfo = new FileInfo(input);
if (!inputInfo.Exists || inputInfo.Length is < 128 or > 96L * 1024 * 1024)
{
    Console.Error.WriteLine("The PDF is absent or outside the inspector bound.");
    return 3;
}

using var document = PdfDocument.Open(input);
if (document.NumberOfPages is < 1 or > 2_000)
{
    Console.Error.WriteLine("The PDF page count is outside the inspector bound.");
    return 4;
}

var pages = new List<object>(document.NumberOfPages);
var totalWords = 0;
for (var number = 1; number <= document.NumberOfPages; number++)
{
    var page = document.GetPage(number);
    if (page.Width <= 0 || page.Height <= 0)
    {
        Console.Error.WriteLine("The PDF contains invalid page geometry.");
        return 5;
    }

    var words = page.GetWords().ToArray();
    totalWords = checked(totalWords + words.Length);
    if (totalWords > 50_000)
    {
        Console.Error.WriteLine("The PDF exceeds the word-inspection bound.");
        return 6;
    }

    pages.Add(new
    {
        page_number = number,
        width = page.Width,
        height = page.Height,
        text = string.Concat(page.Letters.Select(letter => letter.Value)),
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
        pages,
    }, new JsonSerializerOptions { WriteIndented = true });
    await File.WriteAllTextAsync(temporary, json);
    File.Move(temporary, output, overwrite: true);
}
finally
{
    if (File.Exists(temporary)) File.Delete(temporary);
}

return 0;
