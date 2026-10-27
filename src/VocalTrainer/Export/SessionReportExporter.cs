using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace VocalTrainer.Export;

public sealed record ReportOptions(bool IncludeNotes = true, bool IncludeDeviceInfo = true, bool IncludeHistory = true);
public sealed record ReportMetric(string Mode, int TargetMidi, double ToleranceCents, string Reference,
    int Samples, int ReliableSamples, int AccurateSamples, double CoveragePercent, double? AccuracyPercent,
    double? MeanAbsoluteCents, double? MedianAbsoluteCents);
public sealed record ReportExportResult(string FilePath, string SessionId, int PitchSamples, IReadOnlyList<string> Warnings);

/// <summary>Creates a numerical, versioned report. Never copies recordings or source files into the archive.</summary>
public static class SessionReportExporter
{
    public const string Format = "VocalTrainer.Session";
    public const string SchemaVersion = "1.0.0";
    internal static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };
    public static readonly string[] PayloadNames = ["session.json", "pitch.csv", "attempts.json", "range.json", "progress.json", "diagnostics.json", "README.txt"];

    public static ReportExportResult Export(ReportSource source, string destination, string dataRoot, ReportOptions? options = null)
    {
        options ??= new();
        destination = Path.GetFullPath(destination);
        if (!Path.GetExtension(destination).Equals(".zip", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Выбери файл с расширением .zip.");
        if (destination.Equals(Path.GetFullPath(source.FilePath), StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Нельзя заменить исходный журнал отчётом.");
        var telemetry = ReportTelemetryReader.Read(source);
        var warnings = telemetry.Warnings.ToList();
        if (telemetry.Status == "interrupted") warnings.Add("Нет события завершения: отчёт содержит только сохранённую часть занятия.");
        var pitches = ReportPitchBuilder.Build(telemetry);
        if (telemetry.Events.Any(e => e.Type is "midi_preview_start" or "ear_question") &&
            !telemetry.Events.Any(e => e.Type is "midi_preview_end" or "ear_playback_start") &&
            !telemetry.Events.Any(e => e.Type == "session_start" && ReportJson.Number(e.Data, "schemaVersion") >= 3))
            warnings.Add("Старый журнал: границы прослушивания MIDI/слухового эталона неизвестны; некоторые образцы тюнера могли записываться во время воспроизведения.");
        if (pitches.Count == 0) warnings.Add("В журнале нет измерений голоса. Доступны только события и сохранённые оценки.");
        if (!telemetry.Events.Any(e => e.Type == "exercise_start" && ReportJson.Get(e.Data, "exercise").ValueKind == JsonValueKind.Object)
            && telemetry.Events.Any(e => e.Type == "exercise_start")) warnings.Add("Старый журнал: полный план упражнения не сохранён.");
        var previous = options.IncludeHistory ? ReadHistory(telemetry, dataRoot, warnings) : [];
        var rangeHistory = options.IncludeHistory ? ReadRangeHistory(telemetry, dataRoot, warnings) : [];
        object[] Events(Func<ReportEvent, bool> predicate) => telemetry.Events.Where(predicate)
            .Where(e => options.IncludeNotes || e.Type != "user_note")
            .Select(e => (object)new { type = e.Type, e.ElapsedMs, e.TimeUtc, data = Clean(e.Data, options) }).ToArray();
        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        void Add(string name, object value) => files.Add(name, JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions));
        Add("session.json", new {
            schemaVersion = SchemaVersion, sessionId = telemetry.SessionId, telemetry.StartedUtc, telemetry.DurationMs,
            status = telemetry.Status, sourceKind = source.IsPractice ? "practice_run" : "microphone_session",
            modes = pitches.Select(p => p.Mode).Distinct().ToArray(),
            events = Events(e => e.Type is not ("pitch_sample" or "free_pitch_sample" or "midi_pitch_sample" or "sequence_pitch_sample")),
            pitchData = "pitch.csv", originalAssessmentAlgorithm = "frame-assessment-v1"
        });
        files.Add("pitch.csv", Encoding.UTF8.GetBytes(ReportPitchBuilder.ToCsv(pitches)));
        Add("attempts.json", new { schemaVersion = SchemaVersion, sessionId = telemetry.SessionId,
            events = Events(e => e.Type.StartsWith("attempt_", StringComparison.Ordinal) || e.Type.StartsWith("sequence_", StringComparison.Ordinal) && e.Type != "sequence_pitch_sample"
                || e.Type.StartsWith("exercise_", StringComparison.Ordinal) || e.Type.StartsWith("ear_", StringComparison.Ordinal)
                || e.Type is "midi_practice_start" or "midi_practice_end" or "midi_seek") });
        Add("range.json", new { schemaVersion = SchemaVersion, sessionId = telemetry.SessionId,
            events = Events(e => e.Type.StartsWith("range_", StringComparison.Ordinal)), previousChecks = rangeHistory });
        Add("progress.json", new { schemaVersion = SchemaVersion, metricVersion = "sample-metrics-v1", sessionId = telemetry.SessionId,
            current = Metrics(pitches), capturedByMode = CapturedByMode(pitches), previousSessions = previous,
            comparisonKeys = new[] { "mode", "targetMidi", "toleranceCents", "reference" },
            denominator = "Coverage = reliableSamples/samples; accuracy = accurateSamples/reliableSamples. Samples are observations, not time. Only eligible samples are included.",
            historyLimit = 20 });
        warnings.Add("Задержка записи/воспроизведения не измерена; временная компенсация не применена.");
        Add("diagnostics.json", new { schemaVersion = SchemaVersion, warnings, latencyCompensationMs = (double?)null,
            minimumConfidence = .65, historyIncluded = options.IncludeHistory,
            events = Events(e => e.Type.Contains("error", StringComparison.OrdinalIgnoreCase) || e.Type.Contains("calibrat", StringComparison.OrdinalIgnoreCase)
                || e.Type is "session_start" or "training_configuration") });
        files.Add("README.txt", Encoding.UTF8.GetBytes(Documentation));
        var manifest = new {
            format = Format, schemaVersion = SchemaVersion, appVersion = "0.5.1", exporterVersion = "1.0.0", sessionId = telemetry.SessionId,
            exportedUtc = DateTimeOffset.UtcNow, audioIncluded = false, options,
            units = new { frequency = "Hz", deviation = "cent", elapsed = "ms", modeTime = "s", rms = "linear full scale", octave = "scientific; C4 = До первой октавы" },
            csv = new { encoding = "UTF-8", delimiter = ",", decimalSeparator = ".", missing = "empty", columns = ReportPitchBuilder.CsvHeader.Split(',') },
            compatibleExerciseSchemaVersion = 1,
            files = files.Select(f => new { name = f.Key, bytes = f.Value.LongLength, sha256 = Convert.ToHexStringLower(SHA256.HashData(f.Value)) }).ToArray()
        };
        Add("manifest.json", manifest);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        string temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var zip = new ZipArchive(stream, ZipArchiveMode.Create))
                foreach (var file in files)
                {
                    using var output = zip.CreateEntry(file.Key, CompressionLevel.Optimal).Open();
                    output.Write(file.Value);
                }
            SessionReportArchive.ReadManifest(temporary);
            File.Move(temporary, destination, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
        return new(destination, telemetry.SessionId, pitches.Count, warnings);
    }

    public static IReadOnlyList<ReportMetric> Metrics(IReadOnlyList<ReportPitch> rows) => rows.Where(p => p.EligibleForAssessment && p.TargetMidi.HasValue)
        .GroupBy(p => (p.Mode, Midi: p.TargetMidi!.Value, p.ToleranceCents, p.Reference))
        .OrderBy(g => g.Key.Mode, StringComparer.Ordinal).ThenBy(g => g.Key.Midi).ThenBy(g => g.Key.ToleranceCents)
        .Select(g => {
            var errors = g.Where(p => p.Cents.HasValue).Select(p => Math.Abs(p.Cents!.Value)).Order().ToArray();
            int reliable = errors.Length, accurate = g.Count(p => p.InTolerance == true);
            return new ReportMetric(g.Key.Mode, g.Key.Midi, g.Key.ToleranceCents, g.Key.Reference, g.Count(), reliable, accurate,
                100.0 * reliable / g.Count(), reliable == 0 ? null : 100.0 * accurate / reliable,
                reliable == 0 ? null : errors.Average(), reliable == 0 ? null : (errors[(reliable - 1) / 2] + errors[reliable / 2]) / 2);
        }).ToArray();

    private static object[] CapturedByMode(IReadOnlyList<ReportPitch> rows) => rows.GroupBy(p => p.Mode)
        .Select(g => (object)new { mode = g.Key, samples = g.Count(), reliableSamples = g.Count(p => p.Reliable),
            coveragePercent = 100.0 * g.Count(p => p.Reliable) / g.Count(),
            includesUnscoredSamples = true }).ToArray();

    private static object[] ReadHistory(ReportTelemetry selected, string root, List<string> warnings)
    {
        if (selected.StartedUtc is null) { warnings.Add("Время начала неизвестно; сравнение с историей недоступно."); return []; }
        var history = new List<(DateTimeOffset Time, object Value)>();
        foreach (var source in ReportTelemetryReader.Catalog(Path.Combine(root, "Logs"), warnings).Where(s => !s.IsPractice && s.Id != selected.Source.Id))
        {
            try
            {
                var item = ReportTelemetryReader.Read(source);
                if (item.StartedUtc is not { } time || time >= selected.StartedUtc || item.Status != "completed") continue;
                var pitches = ReportPitchBuilder.Build(item);
                history.Add((time, new { sessionId = item.SessionId, startedUtc = time,
                    metrics = Metrics(pitches), capturedByMode = CapturedByMode(pitches), warnings = item.Warnings }));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException)
            { warnings.Add($"Сессия истории {source.Id} пропущена: {ex.GetType().Name}."); }
        }
        return history.OrderByDescending(h => h.Time).Take(20).Select(h => h.Value).ToArray();
    }

    private static JsonElement[] ReadRangeHistory(ReportTelemetry selected, string root, List<string> warnings)
    {
        string path = Path.Combine(root, "range-history.json");
        if (!File.Exists(path) || selected.StartedUtc is not { } time) return [];
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            if (document.RootElement.ValueKind != JsonValueKind.Array) throw new JsonException();
            var cutoff = time.AddMilliseconds(selected.DurationMs);
            return document.RootElement.EnumerateArray().Where(e => ReportJson.Utc(e, "timeUtc") is { } utc && utc <= cutoff).TakeLast(500).Select(e => e.Clone()).ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        { warnings.Add("История диапазона недоступна: " + ex.GetType().Name); return []; }
    }

    private static object? Clean(JsonElement value, ReportOptions options, string key = "")
    {
        if (value.ValueKind == JsonValueKind.Object)
            return value.EnumerateObject().Where(p => options.IncludeDeviceInfo || !IsDevice(p.Name))
                .ToDictionary(p => p.Name, p => Clean(p.Value, options, p.Name));
        if (value.ValueKind == JsonValueKind.Array) return value.EnumerateArray().Select(e => Clean(e, options, key)).ToArray();
        if (value.ValueKind == JsonValueKind.String)
        {
            string text = value.GetString()!;
            if (key.Contains("path", StringComparison.OrdinalIgnoreCase) || key.Equals("fileName", StringComparison.OrdinalIgnoreCase)) return Path.GetFileName(text);
            if (key.Contains("message", StringComparison.OrdinalIgnoreCase) || key.Contains("error", StringComparison.OrdinalIgnoreCase))
                return Regex.Replace(text, @"(?:[A-Za-z]:[\\/]|\\\\)[^\r\n]*", "[path]");
            return text;
        }
        return value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined ? null : value;
    }
    private static bool IsDevice(string key) => key.Equals("microphoneName", StringComparison.OrdinalIgnoreCase)
        || key.Equals("deviceName", StringComparison.OrdinalIgnoreCase) || key.Equals("deviceId", StringComparison.OrdinalIgnoreCase);

    public const string Documentation = """
        VocalTrainer.Session, schema 1.0.0
        UTF-8 JSON and RFC 4180 CSV. No recordings, MIDI files or lyrics files are included.
        manifest.json: format version, options, units and SHA-256 of every payload.
        session.json: saved events, configuration, exercise plans, summaries and optional personal notes.
        pitch.csv: one canonical row per saved measurement, including silence. C4 = До первой октавы.
        attempts.json: original attempts, phrase plans, per-note scores, retries, skips and MIDI results.
        range.json: checks from this session and optionally earlier saved checks (not future checks).
        progress.json: observation-based metrics by mode, target, tolerance and reference plus up to 20 earlier completed sessions.
        diagnostics.json: missing data, corruption warnings, calibration, errors and optional device information.
        elapsed_ms is relative to the session/run. mode_seconds uses the exercise/MIDI/free singing clock where available.
        Missing values are null in JSON and empty CSV cells. They never mean zero.
        reliable requires a positive detected frequency and confidence >= 0.65. Silence remains in coverage denominator.
        in_tolerance = abs(deviation_cents) <= tolerance_cents. Deviation = 1200*log2(detected_hz/target_hz).
        eligible_for_assessment excludes preview, count-in, pauses, gaps and discarded takes. These samples remain available for diagnostics.
        Free singing compares against the nearest tempered note, not an intended melody. Compare only matching metric keys.
        capturedByMode includes all saved observations (including unscored samples and free singing silence with no target); it is not an accuracy score.
        Original frame-based scores and derived sample metrics have different denominators. Do not compare them directly.
        Original assessments may contain timeToLockSeconds and longestStableSeconds; missing legacy values cannot be reconstructed reliably.
        Timing and displayed pitch are not latency-compensated. No vocal health or technique safety assessment is made.
        Interrupted logs export the saved portion with warnings. Practice-only runs may contain no microphone measurements.
        Schema readers should reject unsupported major versions, tolerate unknown fields within major version 1 and verify hashes before analysis.
        Personal notes and device identifiers can be excluded in the export dialog. Review notes before sharing.
        Exercise JSON uses its own schemaVersion (currently 1), independent of the report version.
        """;
}

public static class SessionReportArchive
{
    public static JsonElement ReadManifest(string path)
    {
        using var zip = ZipFile.OpenRead(path);
        if (zip.Entries.Select(e => e.FullName).Distinct(StringComparer.OrdinalIgnoreCase).Count() != zip.Entries.Count) throw new InvalidDataException("Повторяющиеся файлы отчёта.");
        var manifestEntry = zip.GetEntry("manifest.json") ?? throw new InvalidDataException("Нет manifest.json.");
        if (manifestEntry.Length > 1024 * 1024) throw new InvalidDataException("Слишком большой манифест.");
        using var stream = manifestEntry.Open();
        using var document = JsonDocument.Parse(stream);
        var manifest = document.RootElement;
        string? version = ReportJson.String(manifest, "schemaVersion");
        if (ReportJson.String(manifest, "format") != SessionReportExporter.Format || !Version.TryParse(version, out var parsed) || parsed.Major != 1)
            throw new InvalidDataException("Неподдерживаемый формат или версия отчёта.");
        var declarations = ReportJson.Get(manifest, "files");
        if (declarations.ValueKind != JsonValueKind.Array) throw new InvalidDataException("Нет списка файлов.");
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "manifest.json" };
        foreach (var file in declarations.EnumerateArray())
        {
            string name = ReportJson.String(file, "name") ?? "";
            if (name.Length == 0 || name.Contains('/') || name.Contains('\\') || name.Contains(':') || name is "." or ".." || !names.Add(name)) throw new InvalidDataException("Некорректное имя файла.");
            var entry = zip.GetEntry(name) ?? throw new InvalidDataException("Отсутствует файл " + name);
            if (entry.Length != ReportJson.Number(file, "bytes") || entry.Length > ReportTelemetryReader.MaximumFileBytes * 4L) throw new InvalidDataException("Размер файла не совпадает: " + name);
            using var input = entry.Open();
            string hash = Convert.ToHexStringLower(SHA256.HashData(input));
            if (!hash.Equals(ReportJson.String(file, "sha256"), StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Повреждён файл " + name);
        }
        if (names.Count != zip.Entries.Count || SessionReportExporter.PayloadNames.Any(n => !names.Contains(n))) throw new InvalidDataException("Неполный состав отчёта.");
        return manifest.Clone();
    }
}
