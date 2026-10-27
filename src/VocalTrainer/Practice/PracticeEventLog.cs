using System.IO;
using System.Text.Json;

namespace VocalTrainer.Practice;

/// <summary>Practice telemetry only. Never stores or creates microphone audio.</summary>
public sealed class PracticeEventLog
{
    private readonly object _sync = new();
    private readonly string _path;
    private readonly string _runId = Guid.NewGuid().ToString("N");
    private readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();
    private static readonly JsonSerializerOptions Options = new(ExerciseIO.Options) { WriteIndented = false };

    public PracticeEventLog(string directory)
    {
        Directory.CreateDirectory(directory);
        _path = Path.Combine(directory, $"practice-{DateTime.Now:yyyyMMdd}.jsonl");
    }

    public string FilePath => _path;
    public void Write(string eventType, object payload, string? sessionId = null)
    {
        var entry = new { schemaVersion = 2, timeUtc = DateTimeOffset.UtcNow, elapsedMs = _clock.ElapsedMilliseconds, runId = _runId, sessionId, eventType, payload };
        var line = JsonSerializer.Serialize(entry, Options) + Environment.NewLine;
        lock (_sync) File.AppendAllText(_path, line);
    }
}
