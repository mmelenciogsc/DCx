var ffmpegMode = args.Contains("-i", StringComparer.Ordinal) &&
    (args.Contains("-frames:v", StringComparer.Ordinal) || args.Contains("-c:a", StringComparer.Ordinal));
if (ffmpegMode)
{
    var inputIndex = Array.IndexOf(args, "-i");
    if (inputIndex < 0 || inputIndex + 1 >= args.Length || args.Length == 0) return 4;
    var input = args[inputIndex + 1];
    var output = args[^1];
    if (!File.Exists(input)) return 5;
    if (Path.GetFileName(input).StartsWith("timeout", StringComparison.OrdinalIgnoreCase))
        await Task.Delay(TimeSpan.FromSeconds(10));
    Directory.CreateDirectory(Path.GetDirectoryName(output)!);
    if (Path.GetFileName(input).StartsWith("invalid-output", StringComparison.OrdinalIgnoreCase))
        await File.WriteAllBytesAsync(output, [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12]);
    else if (args.Contains("-frames:v", StringComparer.Ordinal))
        await File.WriteAllBytesAsync(output, [0x89, (byte)'P', (byte)'N', (byte)'G', 0x0d, 0x0a, 0x1a, 0x0a, 0]);
    else
        await File.WriteAllBytesAsync(output, [(byte)'R', (byte)'I', (byte)'F', (byte)'F', 4, 0, 0, 0, (byte)'W', (byte)'A', (byte)'V', (byte)'E']);
    return 0;
}

var arguments = args
    .Select((value, index) => (value, index))
    .ToDictionary(item => item.value, item => item.index, StringComparer.Ordinal);

string RequiredValue(string name)
{
    if (!arguments.TryGetValue(name, out var index) || index + 1 >= args.Length)
    {
        throw new InvalidOperationException($"Missing fixture argument {name}.");
    }

    return args[index + 1];
}

var model = RequiredValue("-m");
var audio = RequiredValue("-f");
var outputPrefix = RequiredValue("-of");
if (!File.Exists(model) || !File.Exists(audio) || !args.Contains("-otxt", StringComparer.Ordinal))
{
    return 3;
}

if (Path.GetFileName(audio).StartsWith("timeout", StringComparison.OrdinalIgnoreCase))
{
    await Task.Delay(TimeSpan.FromSeconds(10));
}
else if (!Path.GetFileName(audio).StartsWith("instant", StringComparison.OrdinalIgnoreCase))
{
    // Keep the normal fixture alive long enough to exercise resource sampling.
    // The instant fixture separately verifies honest handling of processes that
    // finish before Windows exposes a usable working-set observation.
    await Task.Delay(TimeSpan.FromMilliseconds(250));
}

if (Path.GetFileName(audio).StartsWith("control", StringComparison.OrdinalIgnoreCase))
{
    await File.WriteAllTextAsync($"{outputPrefix}.txt", "DCx\u0001transcript");
}
else if (Path.GetFileName(audio).StartsWith("invalid-utf8", StringComparison.OrdinalIgnoreCase))
{
    await File.WriteAllBytesAsync($"{outputPrefix}.txt", [0xff, 0xfe, 0xfd]);
}
else
{
    await File.WriteAllTextAsync($"{outputPrefix}.txt", "DCx transcription fixture 42.");
}
return 0;
