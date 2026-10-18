using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace DCx.Core.LocalModels;

public sealed record WhisperRuntimeArchiveInventory(
    int FileCount,
    int DirectoryCount,
    long ExpandedBytes,
    string WhisperCliEntry,
    int LicenseNoticeEntryCount,
    IReadOnlyList<string> LicenseNoticeEntries);

internal static class WhisperRuntimeArchiveInspector
{
    private const int MaximumEntries = 1024;
    private const int MaximumPathCharacters = 240;
    private const int MaximumSegmentCharacters = 100;
    private const long MaximumEntryBytes = 1536L * 1024 * 1024;
    private const long MaximumExpandedBytes = 3L * 1024 * 1024 * 1024;
    private const long MaximumCompressionRatio = 500;
    private const int MaximumReportedLicenseEntries = 32;

    private static readonly HashSet<string> ReservedDeviceNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "AUX", "CLOCK$", "CON", "NUL", "PRN",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    private static readonly uint[] Crc32Table = BuildCrc32Table();

    public static async Task<WhisperRuntimeArchiveInventory> InspectAsync(
        Stream archiveStream,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(archiveStream);
        if (!archiveStream.CanRead || !archiveStream.CanSeek)
        {
            throw new InvalidOperationException("The Whisper runtime archive stream is not reviewable.");
        }

        try
        {
            archiveStream.Position = 0;
            using var archive = new ZipArchive(archiveStream, ZipArchiveMode.Read, leaveOpen: true);
            if (archive.Entries.Count is < 1 or > MaximumEntries)
            {
                throw new InvalidOperationException("The Whisper runtime archive entry count is outside its limit.");
            }

            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var cliEntries = new List<string>();
            var licenseEntries = new List<string>();
            var fileCount = 0;
            var directoryCount = 0;
            long expandedBytes = 0;
            var buffer = new byte[1024 * 1024];
            try
            {
                foreach (var entry in archive.Entries)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var review = ValidateEntry(entry, names);
                    if (review.IsDirectory)
                    {
                        directoryCount++;
                        continue;
                    }

                    fileCount++;
                    expandedBytes = checked(expandedBytes + entry.Length);
                    if (expandedBytes > MaximumExpandedBytes)
                    {
                        throw new InvalidOperationException("The Whisper runtime archive expands beyond its limit.");
                    }
                    if (string.Equals(review.FileName, "whisper-cli.exe", StringComparison.OrdinalIgnoreCase))
                    {
                        cliEntries.Add(review.CanonicalPath);
                    }
                    if (IsLicenseOrNotice(review.FileName))
                    {
                        licenseEntries.Add(review.CanonicalPath);
                    }

                    await using var content = entry.Open();
                    long observed = 0;
                    var crc32 = uint.MaxValue;
                    while (true)
                    {
                        var read = await content.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                        if (read == 0) break;
                        observed = checked(observed + read);
                        crc32 = UpdateCrc32(crc32, buffer.AsSpan(0, read));
                        if (observed > entry.Length || observed > MaximumEntryBytes)
                        {
                            throw new InvalidOperationException("A Whisper runtime archive entry exceeded its reviewed size.");
                        }
                    }
                    if (observed != entry.Length)
                    {
                        throw new InvalidOperationException("A Whisper runtime archive entry length changed during review.");
                    }
                    if (~crc32 != entry.Crc32)
                    {
                        throw new InvalidOperationException("A Whisper runtime archive entry failed its CRC-32 integrity check.");
                    }
                }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(buffer);
            }

            if (fileCount == 0 || cliEntries.Count != 1)
            {
                throw new InvalidOperationException(
                    "The Whisper runtime archive must contain exactly one regular whisper-cli.exe file.");
            }
            var orderedLicenses = licenseEntries.Order(StringComparer.OrdinalIgnoreCase).ToArray();
            var reportedLicenses = orderedLicenses.Take(MaximumReportedLicenseEntries).ToArray();

            return new WhisperRuntimeArchiveInventory(
                fileCount,
                directoryCount,
                expandedBytes,
                cliEntries[0],
                orderedLicenses.Length,
                Array.AsReadOnly(reportedLicenses));
        }
        catch (InvalidOperationException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is ArgumentException or InvalidDataException or IOException or NotSupportedException or
                OverflowException)
        {
            throw new InvalidOperationException("The Whisper runtime archive is malformed or unreadable.", exception);
        }
        finally
        {
            if (archiveStream.CanSeek) archiveStream.Position = 0;
        }
    }

    internal static EntryReview ValidateEntry(ZipArchiveEntry entry, HashSet<string> names)
    {
        var raw = entry.FullName;
        if (string.IsNullOrEmpty(raw) || raw.Length > MaximumPathCharacters ||
            raw != raw.Normalize(NormalizationForm.FormC) || raw.Contains('\\') || raw.Contains(':') ||
            raw.StartsWith("/", StringComparison.Ordinal) || raw.Contains("//", StringComparison.Ordinal) ||
            raw.Any(char.IsControl))
        {
            throw new InvalidOperationException("The Whisper runtime archive contains an unsafe path.");
        }

        var isDirectory = raw.EndsWith("/", StringComparison.Ordinal);
        var canonical = isDirectory ? raw[..^1] : raw;
        if (canonical.Length == 0 || !names.Add(canonical))
        {
            throw new InvalidOperationException("The Whisper runtime archive contains a duplicate or empty path.");
        }
        var segments = canonical.Split('/');
        foreach (var segment in segments)
        {
            if (segment.Length is < 1 or > MaximumSegmentCharacters || segment is "." or ".." ||
                segment != segment.TrimEnd(' ', '.') || IsReservedDeviceName(segment))
            {
                throw new InvalidOperationException("The Whisper runtime archive contains an unsafe path segment.");
            }
        }

        var unixType = (entry.ExternalAttributes >> 16) & 0xF000;
        var windowsAttributes = (FileAttributes)(entry.ExternalAttributes & 0xFFFF);
        if ((windowsAttributes & FileAttributes.ReparsePoint) != 0 ||
            unixType is not (0 or 0x4000 or 0x8000) ||
            isDirectory && unixType == 0x8000 || !isDirectory && unixType == 0x4000)
        {
            throw new InvalidOperationException("The Whisper runtime archive contains a linked or special entry.");
        }
        if (isDirectory)
        {
            if (entry.Length != 0 || entry.CompressedLength != 0)
            {
                throw new InvalidOperationException("The Whisper runtime archive directory entry contains data.");
            }
            return new EntryReview(canonical, segments[^1], true);
        }

        if (entry.IsEncrypted || entry.Length is < 0 or > MaximumEntryBytes || entry.CompressedLength < 0 ||
            entry.Length > 0 && entry.CompressedLength == 0 ||
            entry.CompressedLength > 0 && entry.Length / entry.CompressedLength > MaximumCompressionRatio)
        {
            throw new InvalidOperationException("The Whisper runtime archive entry compression or size is unsafe.");
        }
        return new EntryReview(canonical, segments[^1], false);
    }

    private static bool IsReservedDeviceName(string segment)
    {
        var dot = segment.IndexOf('.');
        var stem = dot < 0 ? segment : segment[..dot];
        return ReservedDeviceNames.Contains(stem);
    }

    internal static uint UpdateCrc32(uint crc32, ReadOnlySpan<byte> bytes)
    {
        foreach (var value in bytes)
        {
            crc32 = Crc32Table[(crc32 ^ value) & 0xff] ^ (crc32 >> 8);
        }
        return crc32;
    }

    private static uint[] BuildCrc32Table()
    {
        var table = new uint[256];
        for (uint index = 0; index < table.Length; index++)
        {
            var value = index;
            for (var bit = 0; bit < 8; bit++)
            {
                value = (value & 1) == 0 ? value >> 1 : 0xedb88320U ^ (value >> 1);
            }
            table[index] = value;
        }
        return table;
    }

    private static bool IsLicenseOrNotice(string fileName)
    {
        var upper = fileName.ToUpperInvariant();
        return upper is "LICENSE" or "COPYING" or "NOTICE" ||
            upper.StartsWith("LICENSE.", StringComparison.Ordinal) ||
            upper.StartsWith("LICENSE-", StringComparison.Ordinal) ||
            upper.StartsWith("COPYING.", StringComparison.Ordinal) ||
            upper.StartsWith("NOTICE.", StringComparison.Ordinal) ||
            upper.StartsWith("NOTICE-", StringComparison.Ordinal) ||
            upper.Contains("THIRD-PARTY", StringComparison.Ordinal) ||
            upper.Contains("THIRDPARTY", StringComparison.Ordinal);
    }

    internal sealed record EntryReview(string CanonicalPath, string FileName, bool IsDirectory);
}
