using System.IO;
using System.Text.Json;

namespace VocalTrainer.Practice;

/// <summary>A compact local report, independent of microphone WAV recording.</summary>
public sealed class RangeHistoryStore
{
    private readonly string _file;
    public RangeHistoryStore(string folder)
    {
        Directory.CreateDirectory(folder);
        _file = Path.Combine(folder, "range-history.json");
    }

    public IReadOnlyList<RangeHistoryEntry> Load()
    {
        if (!File.Exists(_file)) return [];
        try
        {
            var entries = JsonSerializer.Deserialize<List<RangeHistoryEntry>>(File.ReadAllText(_file), ExerciseIO.Options);
            return (entries ?? []).Where(x => x is not null && x.Midi is >= 0 and <= 127).TakeLast(500).ToArray();
        }
        catch (JsonException) { return []; }
        catch (IOException) { return []; }
    }

    public void Append(RangeHistoryEntry entry)
    {
        var list = Load().Append(entry).TakeLast(500).ToList();
        string temporary = _file + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(list, ExerciseIO.Options));
            File.Move(temporary, _file, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}

public sealed record RangeHistoryEntry(DateTimeOffset TimeUtc, int Midi, double CoveragePercent,
    double? MedianCents, double? StabilityCents, string Comfort);
