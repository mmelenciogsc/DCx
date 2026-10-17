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

await File.WriteAllTextAsync($"{outputPrefix}.txt", "DCx transcription fixture 42.");
return 0;
