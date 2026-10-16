using System.Text.Json;

namespace DCx.Core.Orchestration;

public sealed class JsonLineBossCheckpointStore(string filePath) : IBossCheckpointStore, IDisposable
{
    private readonly string _filePath = Path.GetFullPath(filePath);
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task AppendAsync(BossCheckpoint checkpoint, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);
        var directory = Path.GetDirectoryName(_filePath)
            ?? throw new InvalidOperationException("The checkpoint path has no parent directory.");
        Directory.CreateDirectory(directory);
        var json = JsonSerializer.Serialize(checkpoint);

        await _gate.WaitAsync(cancellationToken);
        try
        {
            await File.AppendAllTextAsync(_filePath, json + Environment.NewLine, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose() => _gate.Dispose();
}
