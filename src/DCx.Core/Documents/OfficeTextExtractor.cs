using System.IO.Compression;
using System.Xml;
using System.Xml.Linq;
using DCx.Core.Content;

namespace DCx.Core.Documents;

public sealed class OfficeTextExtractor
{
    private const int MaximumEntries = 5_000;
    private const long MaximumExpandedBytes = 256L * 1024 * 1024;

    public IReadOnlyList<string> Extract(string path, ContentKind kind)
    {
        if (kind is not (ContentKind.Word or ContentKind.Spreadsheet or ContentKind.Presentation))
            throw new ArgumentException("An OOXML content kind is required.", nameof(kind));
        using var archive = ZipFile.OpenRead(Path.GetFullPath(path));
        if (archive.Entries.Count > MaximumEntries || archive.Entries.Sum(entry => entry.Length) > MaximumExpandedBytes)
            throw new InvalidDataException("The Office package exceeds safe expansion limits.");

        return kind switch
        {
            ContentKind.Word => ReadXmlEntries(archive, entry => entry.FullName == "word/document.xml"),
            ContentKind.Presentation => ReadXmlEntries(archive, entry =>
                entry.FullName.StartsWith("ppt/slides/slide", StringComparison.Ordinal) && entry.FullName.EndsWith(".xml", StringComparison.Ordinal)),
            ContentKind.Spreadsheet => ReadSpreadsheet(archive),
            _ => throw new InvalidOperationException(),
        };
    }

    private static IReadOnlyList<string> ReadXmlEntries(ZipArchive archive, Func<ZipArchiveEntry, bool> selector) =>
        archive.Entries.Where(selector).OrderBy(entry => entry.FullName, StringComparer.Ordinal)
            .SelectMany(entry => ReadDocument(entry).Descendants().Where(node => node.Name.LocalName == "t"))
            .Select(node => node.Value).Where(value => !string.IsNullOrWhiteSpace(value)).ToArray();

    private static IReadOnlyList<string> ReadSpreadsheet(ZipArchive archive)
    {
        var sharedEntry = archive.GetEntry("xl/sharedStrings.xml");
        var shared = sharedEntry is null ? [] : ReadDocument(sharedEntry).Descendants()
            .Where(node => node.Name.LocalName == "si")
            .Select(item => string.Concat(item.Descendants().Where(node => node.Name.LocalName == "t").Select(node => node.Value)))
            .ToArray();
        var values = new List<string>();
        foreach (var entry in archive.Entries.Where(entry => entry.FullName.StartsWith("xl/worksheets/sheet", StringComparison.Ordinal) && entry.FullName.EndsWith(".xml", StringComparison.Ordinal)).OrderBy(entry => entry.FullName, StringComparer.Ordinal))
        {
            foreach (var cell in ReadDocument(entry).Descendants().Where(node => node.Name.LocalName == "c"))
            {
                var value = cell.Descendants().FirstOrDefault(node => node.Name.LocalName is "v" or "t")?.Value;
                if (cell.Attribute("t")?.Value == "s" && int.TryParse(value, out var index) && index >= 0 && index < shared.Length) value = shared[index];
                if (!string.IsNullOrWhiteSpace(value)) values.Add(value);
            }
        }
        return values;
    }

    private static XDocument ReadDocument(ZipArchiveEntry entry)
    {
        using var stream = entry.Open();
        using var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 16_000_000 });
        return XDocument.Load(reader, LoadOptions.None);
    }
}
