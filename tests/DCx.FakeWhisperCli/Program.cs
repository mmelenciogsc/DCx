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
    await Task.Delay(TimeSpan.FromMilliseconds(75));
}

await File.WriteAllTextAsync($"{outputPrefix}.txt", "DCx transcription fixture 42.");
return 0;
