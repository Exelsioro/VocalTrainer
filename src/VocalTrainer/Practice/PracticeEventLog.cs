using System.IO;
using System.Text.Json;

namespace VocalTrainer.Practice;

/// <summary>Practice telemetry only. Never stores or creates microphone audio.</summary>
public sealed class PracticeEventLog
{
    private readonly object _sync = new();
    private readonly string _path;
    private readonly string _runId = Guid.NewGuid().ToString("N");

    public PracticeEventLog(string directory)
    {
        Directory.CreateDirectory(directory);
        _path = Path.Combine(directory, $"practice-{DateTime.Now:yyyyMMdd}.jsonl");
    }

    public string FilePath => _path;
    public void Write(string eventType, object payload)
    {
        var entry = new { schemaVersion = 1, timeUtc = DateTimeOffset.UtcNow, runId = _runId, eventType, payload };
        var line = JsonSerializer.Serialize(entry, ExerciseIO.Options) + Environment.NewLine;
        lock (_sync) File.AppendAllText(_path, line);
    }
}
