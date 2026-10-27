using System.Globalization;
using System.IO.Compression;
using System.Text.Json;
using VocalTrainer;
using VocalTrainer.Export;
using VocalTrainer.Practice;
using VocalTrainer.ViewModels;

internal static class ExportTests
{
    public static void Run(Action<bool, string> check)
    {
        string root = Path.Combine(Path.GetTempPath(), "vocal-export-" + Guid.NewGuid().ToString("N"));
        string logs = Path.Combine(root, "Logs"); Directory.CreateDirectory(logs);
        void Write(string path, params object[] records) => File.WriteAllLines(path, records.Select(r => JsonSerializer.Serialize(r)));
        string ReadZip(string path, string name) { using var zip = ZipFile.OpenRead(path); using var reader = new StreamReader(zip.GetEntry(name)!.Open()); return reader.ReadToEnd(); }
        JsonElement JsonZip(string path, string name) { using var doc = JsonDocument.Parse(ReadZip(path, name)); return doc.RootElement.Clone(); }
        void Reject(Action action, string message) { try { action(); } catch (Exception ex) when (ex is InvalidDataException or FormatException or JsonException or IOException or ArgumentException) { check(true, message); return; } throw new Exception(message); }
        var originalCulture = CultureInfo.CurrentCulture;
        try
        {
            string older = Path.Combine(logs, "session-20260101.jsonl");
            Write(older, new { type = "session_start", details = new { sessionId = "older", timestampUtc = "2026-01-01T00:00:00Z" } },
                new { type = "pitch_sample", elapsedMs = 100, phase = "manual", targetMidi = 69, detectedHz = 440, confidence = .99 }, new { type = "session_end" });
            string future = Path.Combine(logs, "session-20270101.jsonl");
            Write(future, new { type = "session_start", details = new { timestampUtc = "2027-01-01T00:00:00Z" } }, new { type = "session_end" });
            string current = Path.Combine(logs, "session-20260201.jsonl");
            Write(current,
                new { type = "session_start", details = new { sessionId = "selected", timestampUtc = "2026-02-01T00:00:00Z", microphoneName = "Secret device", sampleRate = 44100 } },
                new { type = "training_configuration", details = new { selectedTolerance = 40 } },
                new { type = "user_note", details = new { text = "Private note" } },
                new { type = "pitch_sample", elapsedMs = 100, phase = "manual", targetMidi = 69, detectedHz = 440.0, confidence = .99, rms = .1 },
                new { type = "pitch_sample", elapsedMs = 220, phase = "manual", targetMidi = 69, detectedHz = (double?)null, confidence = 0 },
                new { type = "pitch_sample", elapsedMs = 340, phase = "manual", targetMidi = 69, detectedHz = 880.0, confidence = .99 },
                new { type = "study_range_start", details = new { midi = 60 } },
                new { type = "pitch_sample", elapsedMs = 460, phase = "manual", targetMidi = 69, detectedHz = NoteCatalog.ToFrequency(60), confidence = .99 },
                new { type = "study_range_measured", details = new { midi = 60 } },
                new { type = "study_midi_practice_start", details = new { targets = new[] { new { midi = 62, startSeconds = 0, durationSeconds = 1 } } } },
                new { type = "study_midi_pitch_sample", elapsedMs = 580, details = new { targetMidi = 62, seconds = .2, frequencyHz = NoteCatalog.ToFrequency(62), confidence = .99 } },
                new { type = "study_midi_seek", details = new { scoringReset = true } },
                new { type = "study_midi_pitch_sample", elapsedMs = 700, details = new { targetMidi = 62, seconds = .1, frequencyHz = NoteCatalog.ToFrequency(62), confidence = .99 } },
                new { type = "study_midi_practice_end", details = new { status = "completed" } },
                new { type = "free_pitch_sample", elapsedMs = 820, details = new { FreeElapsedMs = 120, FrequencyHz = 440.0, Midi = 69, confidence = .99 } },
                new { type = "audio_error", details = new { message = @"Cannot read C:\Users\secret\file.wav", deviceId = 42 } },
                new { type = "future_event", details = new { value = 17, filePath = @"C:\Users\secret\exercise.json" } },
                new { type = "session_end", elapsedMs = 1000, details = new { status = "completed" } });
            string archive = Path.Combine(root, "report.zip"); var source = new ReportSource("session-20260201", current);
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("ru-RU");
            var result = SessionReportExporter.Export(source, archive, root);
            var manifest = SessionReportArchive.ReadManifest(archive);
            check(ReportJson.String(manifest, "sessionId") == "selected" && manifest.GetProperty("audioIncluded").GetBoolean() == false &&
                manifest.GetProperty("files").GetArrayLength() == 7, "export contains versioned manifest and seven hashed numerical payloads, no audio");
            check(result.PitchSamples == 7, "export keeps one canonical row per microphone observation across tuner, range, MIDI and free singing");
            var telemetry = ReportTelemetryReader.Read(source); var rows = ReportPitchBuilder.Build(telemetry);
            check(rows[3].TargetMidi == 60 && rows[3].Mode == "range" && Math.Abs(rows[3].Cents!.Value) < .01, "range export uses measured target, not unrelated tuner target");
            check(!rows[4].EligibleForAssessment && rows[5].EligibleForAssessment && rows[4].Take != rows[5].Take, "MIDI seek excludes discarded take from derived metrics without losing samples");
            var metrics = SessionReportExporter.Metrics(rows); var tuner = metrics.Single(m => m.Mode == "tuner");
            check(tuner.Samples == 3 && tuner.ReliableSamples == 2 && tuner.AccuracyPercent == 50 && tuner.MeanAbsoluteCents == 600 && tuner.ToleranceCents == 25,
                "sample metrics expose silence, octave error and explicit denominators; manual tolerance remains 25 cents");
            check(metrics.Any(m => m.Mode == "free_singing" && m.Reference == "nearest_equal_tempered_note"), "free singing has separate comparison key from fixed melody accuracy");
            string csv = ReadZip(archive, "pitch.csv");
            check(csv.Contains("0.99") && csv.Contains("A4,4,440") && csv.Split("\r\n", StringSplitOptions.RemoveEmptyEntries).All(l => l.Split(',').Length == ReportPitchBuilder.CsvHeader.Split(',').Length),
                "CSV uses invariant decimals, named notes, explicit octaves and consistent columns in Russian locale");
            var progress = JsonZip(archive, "progress.json");
            check(progress.GetProperty("previousSessions").GetArrayLength() == 1 && ReportJson.String(progress.GetProperty("previousSessions")[0], "sessionId") == "older",
                "progress includes only earlier completed sessions with consistent session identifiers");
            check(ReadZip(archive, "session.json").Contains("future_event") && !ReadZip(archive, "session.json").Contains("Users") &&
                ReadZip(archive, "session.json").Contains("Private note"), "unknown events survive export while machine paths are removed");
            var history = new RangeHistoryStore(root);
            history.Append(new(DateTimeOffset.Parse("2026-01-01T00:00:00Z"), 60, 100, 0, 1, "comfortable"));
            history.Append(new(DateTimeOffset.Parse("2027-01-01T00:00:00Z"), 61, 100, 0, 1, "future"));
            SessionReportExporter.Export(source, archive, root, new(false, false, true));
            check(!ReadZip(archive, "session.json").Contains("Private note") && !ReadZip(archive, "diagnostics.json").Contains("Secret device") &&
                !ReadZip(archive, "session.json").Contains("deviceId") && ReadZip(archive, "session.json").Contains("sampleRate"), "privacy options remove user notes and device identifiers but preserve analysis configuration");
            check(JsonZip(archive, "range.json").GetProperty("previousChecks").GetArrayLength() == 1, "range history excludes checks made after selected session");
            SessionReportExporter.Export(source, archive, root, new(false, false, false));
            check(JsonZip(archive, "progress.json").GetProperty("previousSessions").GetArrayLength() == 0 && JsonZip(archive, "range.json").GetProperty("previousChecks").GetArrayLength() == 0,
                "history opt-out excludes previous sessions and saved range history");
            string broken = Path.Combine(logs, "session-broken.jsonl");
            File.WriteAllText(broken, "{\"type\":\"session_start\",\"details\":{\"timestampUtc\":\"2026-02-02T00:00:00Z\"}}\n{bad\n{\"type\":\"user_note\",\"details\":{\"text\":\"recovered\"}}\n{\"type\":");
            var partial = ReportTelemetryReader.Read(new("session-broken", broken));
            check(partial.Status == "interrupted" && partial.Events.Count == 2 && partial.Warnings.Count == 2, "reader recovers valid records after corruption and reports truncated tail");
            SessionReportExporter.Export(partial.Source, archive, root);
            check(ReadZip(archive, "session.json").Contains("interrupted") && ReadZip(archive, "diagnostics.json").Contains("warnings") &&
                JsonZip(archive, "progress.json").GetProperty("current").GetArrayLength() == 0, "interrupted session exports saved data and empty metrics rather than fabricated zero scores");
            var emptyVoice = rows.Take(1).Select(p => p with { DetectedHz = null, Cents = null, Reliable = false, InTolerance = null }).ToArray();
            check(SessionReportExporter.Metrics(emptyVoice)[0].AccuracyPercent is null && SessionReportExporter.Metrics(emptyVoice)[0].CoveragePercent == 0, "no reliable voice yields null accuracy and zero coverage");
            string practice = Path.Combine(logs, "practice-20260203.jsonl");
            File.WriteAllText(practice, JsonSerializer.Serialize(new { runId = "old", timeUtc = "2026-02-03T00:00:00Z", eventType = "ear_answer", payload = new { correct = true } }, new JsonSerializerOptions { WriteIndented = true }) + "\n" +
                JsonSerializer.Serialize(new { runId = "other", eventType = "ear_answer", payload = new { correct = false } }));
            var catalog = ReportTelemetryReader.Catalog(logs);
            var standalone = ReportTelemetryReader.Read(catalog.Single(s => s.RunId == "old"));
            check(standalone.Events.Count == 1 && standalone.Events[0].Type == "ear_answer" && standalone.Warnings.Count == 1, "legacy multiline practice logs support separate run exports with duplication warning");
            var practiceLog = new PracticeEventLog(logs);
            practiceLog.Write("ear_answer", new { correct = true }); practiceLog.Write("midi_pitch_sample", new { confidence = .99 }, "selected");
            string[] compact = File.ReadAllLines(practiceLog.FilePath);
            check(compact.Length == 2 && compact.All(l => JsonDocument.Parse(l).RootElement.ValueKind == JsonValueKind.Object), "new practice log writes one complete JSON object per line");
            var newRun = ReportTelemetryReader.Catalog(logs).Single(s => s.FilePath == practiceLog.FilePath);
            check(ReportTelemetryReader.Read(newRun).Events.Count == 1, "practice catalog excludes events already mirrored in microphone session");
            var mirrored = new ReportTelemetry(new("sequence", "unused"), [
                new("exercise_start", 0, null, JsonSerializer.SerializeToElement(new { })),
                new("sequence_attempt_start", 0, null, JsonSerializer.SerializeToElement(new { sectionIndex = 1, targets = new[] { new { toleranceCents = 10 } } })),
                new("phase_change", 0, null, JsonSerializer.SerializeToElement(new { phase = "sequence_hold" })),
                new("sequence_pitch_sample", 100, null, JsonSerializer.SerializeToElement(new { seconds = .1, targetIndex = 0, targetMidi = 60 })),
                new("pitch_sample", 100, null, JsonSerializer.SerializeToElement(new { targetMidi = 69, detectedHz = NoteCatalog.ToFrequency(60), confidence = .99 }))
            ], []);
            var phrase = ReportPitchBuilder.Build(mirrored);
            check(phrase.Count == 1 && phrase[0].TargetMidi == 60 && phrase[0].ToleranceCents == 10 && phrase[0].EligibleForAssessment,
                "sequence mirror enriches one canonical sample with phrase target and per-note tolerance");
            ReportEvent E(string type, object data) => new(type, 100, null, JsonSerializer.SerializeToElement(data));
            object sample = new { targetMidi = 60, detectedHz = NoteCatalog.ToFrequency(60), confidence = .99, phase = "manual" };
            var playback = mirrored with { Events = [
                E("session_start", new { schemaVersion = 3 }),
                E("exercise_start", new { }), E("attempt_start", new { sectionIndex = 2, noteIndex = 4 }), E("exercise_end", new { }),
                E("midi_preview_start", new { }), E("pitch_sample", sample), E("midi_preview_end", new { }),
                E("ear_playback_start", new { }), E("pitch_sample", sample), E("ear_playback_end", new { }), E("pitch_sample", sample),
                E("midi_practice_start", new { }), E("pitch_sample", sample), E("midi_practice_end", new { status = "cancelled_count_in" }), E("pitch_sample", sample)
            ] };
            var referenceRows = ReportPitchBuilder.Build(playback);
            check(referenceRows.Select(p => p.Mode).SequenceEqual(new[] { "midi_preview", "ear_reference", "tuner", "midi_prepare", "tuner" }) &&
                referenceRows.Select(p => p.EligibleForAssessment).SequenceEqual(new[] { false, false, true, false, true }),
                "reference playback and cancelled count-in cannot contaminate derived tuner accuracy");
            check(referenceRows.All(p => p.Section is null && p.NoteIndex is null), "exercise note indices do not leak into subsequent tuner or song samples");
            byte[] good = File.ReadAllBytes(archive);
            string empty = Path.Combine(root, "empty.jsonl"); File.WriteAllText(empty, "");
            Reject(() => SessionReportExporter.Export(new("empty", empty), archive, root), "empty log cannot overwrite existing report");
            check(File.ReadAllBytes(archive).SequenceEqual(good) && !Directory.EnumerateFiles(root, "*.tmp").Any(), "export failure preserves previous ZIP and leaves no temporary files");
            using (var zip = ZipFile.Open(archive, ZipArchiveMode.Update))
            { zip.GetEntry("pitch.csv")!.Delete(); using var writer = new StreamWriter(zip.CreateEntry("pitch.csv").Open()); writer.Write("tampered"); }
            Reject(() => SessionReportArchive.ReadManifest(archive), "archive reader detects altered payload using manifest lengths and hashes");
            File.WriteAllBytes(archive, good);
            void SetVersion(string version)
            {
                var currentManifest = JsonZip(archive, "manifest.json");
                var node = System.Text.Json.Nodes.JsonNode.Parse(currentManifest.GetRawText())!;
                node["schemaVersion"] = version;
                string text = node.ToJsonString();
                using var zip = ZipFile.Open(archive, ZipArchiveMode.Update); zip.GetEntry("manifest.json")!.Delete();
                using var writer = new StreamWriter(zip.CreateEntry("manifest.json").Open()); writer.Write(text);
            }
            SetVersion("1.2.0"); check(ReportJson.String(SessionReportArchive.ReadManifest(archive), "schemaVersion") == "1.2.0", "archive reader tolerates compatible minor schema versions");
            SetVersion("2.0.0");
            Reject(() => SessionReportArchive.ReadManifest(archive), "archive reader rejects unsupported major schema version");
            string fullLog;
            var exercise = new ExerciseDocument { Mode = "sequence", PlayReference = false, Title = "Test phrase", Sections = [new() { Title = "Phrase", Notes = [new() { Note = "C4", HoldSeconds = 1 }] }] };
            var attempt = new AttemptAssessment(60, new(25, .75), 0);
            for (int i = 0; i < 8; i++) attempt.Add(new(NoteCatalog.ToFrequency(60), .1, .99), i * 120);
            using (var logger = new SessionLogger(logs, 60, "Device"))
            {
                fullLog = logger.FilePath;
                logger.Event("exercise_start", new { exercise = JsonSerializer.SerializeToElement(exercise, ExerciseIO.Options) });
                logger.Event("sequence_attempt_assessment", new { result = attempt.Finish() });
                logger.Sample(60, new(NoteCatalog.ToFrequency(60), .1, .99)); logger.Finish("audio_error");
            }
            var fullSource = new ReportSource("real", fullLog);
            SessionReportExporter.Export(fullSource, archive, root, new(false, false, false));
            var saved = JsonZip(archive, "session.json");
            var savedPlan = saved.GetProperty("events").EnumerateArray().Single(e => e.GetProperty("type").GetString() == "exercise_start").GetProperty("data").GetProperty("exercise");
            var compatible = JsonSerializer.Deserialize<ExerciseDocument>(savedPlan.GetRawText(), ExerciseIO.Options)!; ExerciseIO.Validate(compatible);
            check(compatible.Mode == "sequence" && !compatible.PlayReference && compatible.Sections[0].Notes[0].Note == "C4", "exported exercise plan remains compatible with exercise JSON schema");
            check(ReportJson.String(saved, "status") == "audio_error" && ReportJson.String(saved, "sessionId") == ReportTelemetryReader.Read(fullSource).SessionId &&
                ReadZip(archive, "attempts.json").Contains("TimeToLockSeconds") && ReadZip(archive, "attempts.json").Contains("LongestStableSeconds"),
                "real logger roundtrip preserves session identity, error status and original stability/lock measurements");
            File.WriteAllBytes(archive, good);
            using (var zip = ZipFile.Open(archive, ZipArchiveMode.Update))
            { using var writer = new StreamWriter(zip.CreateEntry("unexpected.wav").Open()); writer.Write("not audio"); }
            Reject(() => SessionReportArchive.ReadManifest(archive), "archive reader rejects undeclared extra files");
            bool available = true;
            using var viewModel = new SessionExportViewModel(root, () => available, _ => null);
            viewModel.RefreshAsync(current).GetAwaiter().GetResult();
            check(viewModel.SelectedSource?.FilePath == current && viewModel.CanExport, "export model selects requested completed session");
            available = false; viewModel.RefreshAvailability();
            check(!viewModel.ExportCommand.CanExecute(null), "active training disables export command");
            available = true; viewModel.ExportSelectedAsync().GetAwaiter().GetResult();
            check(!viewModel.IsBusy && viewModel.CanExport, "cancelling save dialog restores export controls");
        }
        finally { CultureInfo.CurrentCulture = originalCulture; Directory.Delete(root, true); }
    }
}
