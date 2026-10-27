using System.IO;
using System.Text.Json;

namespace VocalTrainer.Export;

public sealed record ReportSource(string Id, string FilePath, string? RunId = null)
{
    public bool IsPractice => RunId is not null;
    public string Display => RunId is { } run ? $"{Path.GetFileNameWithoutExtension(FilePath)} · практика {run[..Math.Min(8, run.Length)]}" : Path.GetFileNameWithoutExtension(FilePath);
}
public sealed record ReportEvent(string Type, long? ElapsedMs, DateTimeOffset? TimeUtc, JsonElement Data);
public sealed record ReportTelemetry(ReportSource Source, IReadOnlyList<ReportEvent> Events, IReadOnlyList<string> Warnings)
{
    public string SessionId => ReportJson.String(Events.FirstOrDefault(e => e.Type == "session_start")?.Data ?? default, "sessionId") ?? Source.Id;
    public DateTimeOffset? StartedUtc => Events.FirstOrDefault(e => e.Type == "session_start") is { } start
        ? ReportJson.Utc(start.Data, "timestampUtc") : Events.FirstOrDefault()?.TimeUtc;
    public long DurationMs => Events.Select(e => e.ElapsedMs ?? 0).DefaultIfEmpty(0).Max();
    public string Status => Source.IsPractice ? "practice_run" : Events.LastOrDefault(e => e.Type == "session_end") is { } end
        ? ReportJson.String(end.Data, "status") ?? "completed" : "interrupted";
}

public static class ReportJson
{
    public static JsonElement Get(JsonElement item, string name)
    {
        if (item.ValueKind == JsonValueKind.Object)
            foreach (var property in item.EnumerateObject())
                if (property.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) return property.Value;
        return default;
    }
    public static string? String(JsonElement item, string name) => Get(item, name) is { ValueKind: JsonValueKind.String } value ? value.GetString() : null;
    public static double? Number(JsonElement item, string name) => Get(item, name).TryFinite();
    public static double? TryFinite(this JsonElement value) => value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out double number) && double.IsFinite(number) ? number : null;
    public static DateTimeOffset? Utc(JsonElement item, string name) => DateTimeOffset.TryParse(String(item, name), System.Globalization.CultureInfo.InvariantCulture,
        System.Globalization.DateTimeStyles.None, out var result) ? result.ToUniversalTime() : null;
}

/// <summary>Reads compact JSONL and the historical indented, concatenated JSON records. Recovers after damaged records.</summary>
public static class ReportTelemetryReader
{
    public const int MaximumFileBytes = 128 * 1024 * 1024;
    public static (List<JsonElement> Records, List<string> Warnings) ReadObjects(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (stream.Length > MaximumFileBytes) throw new IOException("Журнал превышает 128 МБ; разбей его на отдельные занятия.");
        byte[] bytes = new byte[checked((int)stream.Length)]; stream.ReadExactly(bytes);
        var records = new List<JsonElement>(); var warnings = new List<string>();
        int offset = bytes.AsSpan().StartsWith(new byte[] { 239, 187, 191 }) ? 3 : 0;
        while (offset < bytes.Length)
        {
            while (offset < bytes.Length && bytes[offset] is 9 or 10 or 13 or 32) offset++;
            if (offset == bytes.Length) break;
            try
            {
                var reader = new Utf8JsonReader(bytes.AsSpan(offset), new JsonReaderOptions { AllowMultipleValues = true, MaxDepth = 64 });
                using var document = JsonDocument.ParseValue(ref reader);
                if (document.RootElement.ValueKind == JsonValueKind.Object) records.Add(document.RootElement.Clone());
                else warnings.Add($"Пропущена запись не в виде объекта, смещение {offset} байт.");
                offset += checked((int)reader.BytesConsumed);
            }
            catch (JsonException)
            {
                warnings.Add($"Повреждённая или оборванная запись, смещение {offset} байт.");
                int next = offset + 1;
                while (next < bytes.Length && !(bytes[next] == (byte)'{' && bytes[next - 1] == (byte)'\n')) next++;
                offset = next;
            }
        }
        return (records, warnings);
    }

    public static ReportTelemetry Read(ReportSource source)
    {
        var (records, warnings) = ReadObjects(source.FilePath);
        var events = new List<ReportEvent>();
        foreach (var record in records)
        {
            if (source.IsPractice && (ReportJson.String(record, "runId") != source.RunId || ReportJson.String(record, "sessionId") is not null)) continue;
            string? type = ReportJson.String(record, source.IsPractice ? "eventType" : "type");
            if (type is null) { warnings.Add("Пропущена запись без типа события."); continue; }
            double? elapsed = ReportJson.Number(record, "elapsedMs");
            var data = ReportJson.Get(record, source.IsPractice ? "payload" : "details");
            if (data.ValueKind == JsonValueKind.Undefined) data = record;
            events.Add(new(type.StartsWith("study_", StringComparison.Ordinal) ? type[6..] : type,
                elapsed is >= 0 and <= long.MaxValue ? (long)elapsed.Value : null,
                ReportJson.Utc(record, "timeUtc"), data));
        }
        if (events.Count == 0) throw new FormatException("В журнале нет читаемых событий выбранного занятия.");
        if (source.IsPractice)
        {
            long origin = events.Where(e => e.ElapsedMs.HasValue).Select(e => e.ElapsedMs!.Value).DefaultIfEmpty(0).Min();
            DateTimeOffset? utcOrigin = events.FirstOrDefault(e => e.TimeUtc.HasValue)?.TimeUtc;
            events = events.Select(e => e with { ElapsedMs = e.ElapsedMs is { } ms ? ms - origin : e.TimeUtc is { } utc && utcOrigin is { } first ? Math.Max(0, (long)(utc - first).TotalMilliseconds) : null }).ToList();
            if (records.Any(r => ReportJson.String(r, "runId") == source.RunId && ReportJson.Get(r, "sessionId").ValueKind == JsonValueKind.Undefined))
                warnings.Add("Старый журнал практики не связывает события с микрофонными сессиями; возможны копии событий в отдельных отчётах.");
        }
        return new(source, events, warnings);
    }

    public static IReadOnlyList<ReportSource> Catalog(string logs, List<string>? warnings = null)
    {
        if (!Directory.Exists(logs)) return [];
        var sources = Directory.EnumerateFiles(logs, "session-*.jsonl").OrderDescending().Select(path => new ReportSource(Path.GetFileNameWithoutExtension(path), path)).ToList();
        foreach (string path in Directory.EnumerateFiles(logs, "practice-*.jsonl").OrderDescending())
        {
            List<JsonElement> records;
            try { (records, _) = ReadObjects(path); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { warnings?.Add($"Не удалось прочитать {Path.GetFileName(path)}: {ex.GetType().Name}"); continue; }
            foreach (string run in records.Where(r => ReportJson.String(r, "sessionId") is null).Select(r => ReportJson.String(r, "runId")).OfType<string>().Distinct())
                sources.Add(new("practice-" + run, path, run));
        }
        return sources.OrderByDescending(s => Path.GetFileName(s.FilePath)[(s.IsPractice ? 9 : 8)..], StringComparer.Ordinal).ToArray();
    }
}
