using System.Globalization;
using System.Text;
using System.Text.Json;

namespace VocalTrainer.Export;

public sealed record ReportPitch(long? ElapsedMs, double? ModeSeconds, string Mode, string Phase, int Take,
    int? Section, int? NoteIndex, int? Lap, int? TargetMidi, double? TargetHz, double? DetectedHz,
    int? DetectedMidi, double? Cents, double? Confidence, double? Rms, bool Reliable, double ToleranceCents,
    bool? InTolerance, bool EligibleForAssessment, string Reference);

public static class ReportPitchBuilder
{
    public const string CsvHeader = "elapsed_ms,mode_seconds,mode,phase,take,section_index,note_index,lap,target_midi,target_note,target_octave,target_hz,detected_hz,detected_midi,detected_note,detected_octave,deviation_cents,confidence,rms,reliable,tolerance_cents,in_tolerance,eligible_for_assessment,reference";
    public static IReadOnlyList<ReportPitch> Build(ReportTelemetry telemetry)
    {
        var rows = new List<ReportPitch>(); string mode = "tuner", phase = "manual";
        int take = 0; int? section = null, note = null, rangeMidi = null;
        double tolerance = 25; bool midiBusy = false; JsonElement sequence = default, pendingSequence = default;
        bool midiPreview = false, earReference = false;
        bool referenceMarkers = telemetry.Events.Any(e => e.Type is "midi_preview_end" or "ear_playback_start") ||
            telemetry.Events.Any(e => e.Type == "session_start" && ReportJson.Number(e.Data, "schemaVersion") >= 3);
        foreach (var entry in telemetry.Events)
        {
            var data = entry.Data;
            int? Integer(string name) => ReportJson.Number(data, name) is { } n && n is >= int.MinValue and <= int.MaxValue ? (int)n : null;
            switch (entry.Type)
            {
                case "training_configuration":
                    tolerance = ReportJson.Number(data, "selectedTolerance") ?? 25;
                    midiPreview = ReportJson.String(data, "referencePlayback") == "midi_preview";
                    earReference = ReportJson.String(data, "referencePlayback") == "ear_reference"; break;
                case "phase_change": phase = ReportJson.String(data, "phase") ?? phase; break;
                case "exercise_start": mode = "exercise"; section = note = null; sequence = pendingSequence = default; break;
                case "exercise_end": mode = "tuner"; section = note = null; break;
                case "attempt_start": take++; section = Integer("sectionIndex"); note = Integer("noteIndex"); tolerance = ReportJson.Number(data, "toleranceCents") ?? 25; break;
                case "sequence_attempt_start":
                    mode = "sequence"; take++; section = Integer("sectionIndex"); sequence = ReportJson.Get(data, "targets"); break;
                case "sequence_pitch_sample": pendingSequence = data; continue;
                case "exercise_attempt_repeated": case "sequence_repeated": case "sequence_skipped": case "exercise_step_skipped":
                    for (int i = rows.Count - 1; i >= 0 && rows[i].Take == take; i--) rows[i] = rows[i] with { EligibleForAssessment = false };
                    break;
                case "range_start": rangeMidi = Integer("midi"); break;
                case "range_measured": case "range_cancel": rangeMidi = null; break;
                case "midi_practice_start": midiBusy = true; take++; break;
                case "midi_practice_end": midiBusy = false; break;
                case "midi_preview_start": midiPreview = referenceMarkers; break;
                case "midi_preview_end": midiPreview = false; break;
                case "ear_playback_start": earReference = true; break;
                case "ear_playback_end": earReference = false; break;
                case "midi_seek":
                    if (ReportJson.Get(data, "scoringReset").ValueKind == JsonValueKind.True)
                    {
                        for (int i = rows.Count - 1; i >= 0 && rows[i].Take == take; i--) rows[i] = rows[i] with { EligibleForAssessment = false };
                        take++;
                    }
                    break;
            }
            if (entry.Type is not ("pitch_sample" or "free_pitch_sample" or "midi_pitch_sample")) continue;
            string rowMode = entry.Type == "free_pitch_sample" ? "free_singing" : entry.Type == "midi_pitch_sample" ? "midi" : rangeMidi.HasValue ? "range" :
                midiBusy ? "midi_prepare" : midiPreview ? "midi_preview" : earReference ? "ear_reference" : mode;
            string rowPhase = ReportJson.String(data, "phase") ?? (rowMode == "midi" ? "sing" : phase);
            double? frequency = ReportJson.Number(data, "detectedHz") ?? ReportJson.Number(data, "frequencyHz") ?? ReportJson.Number(data, "detectedFrequencyHz");
            if (frequency is <= 0) frequency = null;
            double? confidence = ReportJson.Number(data, "confidence");
            bool reliable = frequency.HasValue && confidence is >= .65;
            int? target = rowMode == "free_singing" ? Integer("midi") : rowMode == "range" ? rangeMidi : Integer("targetMidi");
            double? modeSeconds = rowMode == "free_singing" ? ReportJson.Number(data, "freeElapsedMs") / 1000 : rowMode == "midi" ? ReportJson.Number(data, "seconds") : null;
            double rowTolerance = rowMode is "free_singing" or "tuner" ? 25 : rowMode == "midi" ? 40 : rowMode == "range" ? 50 : tolerance;
            int? rowNote = rowMode == "midi" ? Integer("targetIndex") : rowMode is "exercise" or "sequence" ? note : null;
            if (rowMode == "sequence" && pendingSequence.ValueKind == JsonValueKind.Object)
            {
                target = ReportJson.Number(pendingSequence, "targetMidi") is { } t ? (int)t : null;
                rowNote = ReportJson.Number(pendingSequence, "targetIndex") is { } index ? (int)index : null;
                modeSeconds = ReportJson.Number(pendingSequence, "seconds");
                if (rowNote is { } ni && sequence.ValueKind == JsonValueKind.Array && ni >= 0 && ni < sequence.GetArrayLength())
                    rowTolerance = ReportJson.Number(sequence[ni], "toleranceCents") ?? tolerance;
                pendingSequence = default;
            }
            if (target is < 0 or > 127) target = null;
            double? targetHz = target is { } midi ? NoteCatalog.ToFrequency(midi) : null;
            double? cents = reliable && targetHz is { } hz ? 1200 * Math.Log2(frequency!.Value / hz) : null;
            bool eligible = target.HasValue && (rowMode is "free_singing" or "midi" or "range" ||
                ((rowMode is "exercise" or "sequence") && (rowPhase is "hold" or "sequence_hold")) || rowMode == "tuner" && rowPhase == "manual");
            rows.Add(new(entry.ElapsedMs, modeSeconds, rowMode, rowPhase, take, rowMode is "exercise" or "sequence" ? section : null, rowNote,
                Integer("lap"), target, targetHz, frequency, reliable ? NoteCatalog.NearestMidi(frequency!.Value) : null,
                cents, confidence, ReportJson.Number(data, "rms"), reliable, rowTolerance,
                cents is { } error ? Math.Abs(error) <= rowTolerance : null, eligible,
                rowMode == "free_singing" ? "nearest_equal_tempered_note" : "selected_target"));
        }
        return rows;
    }

    public static string ToCsv(IReadOnlyList<ReportPitch> rows)
    {
        var csv = new StringBuilder(CsvHeader + "\r\n");
        foreach (var row in rows)
        {
            object?[] values = [row.ElapsedMs, row.ModeSeconds, row.Mode, row.Phase, row.Take, row.Section,
                row.NoteIndex, row.Lap, row.TargetMidi, row.TargetMidi is { } target ? NoteCatalog.ToLatin(target) : null,
                row.TargetMidi is { } tm ? tm / 12 - 1 : null, row.TargetHz, row.DetectedHz, row.DetectedMidi, row.DetectedMidi is { } detected ? NoteCatalog.ToLatin(detected) : null,
                row.DetectedMidi is { } dm ? dm / 12 - 1 : null,
                row.Cents, row.Confidence, row.Rms, row.Reliable, row.ToleranceCents, row.InTolerance, row.EligibleForAssessment, row.Reference];
            csv.AppendJoin(',', values.Select(Cell)); csv.Append("\r\n");
        }
        return csv.ToString();
    }
    private static string Cell(object? value)
    {
        string text = value switch { null => "", bool flag => flag ? "true" : "false", IFormattable number => number.ToString(null, CultureInfo.InvariantCulture), _ => value.ToString() ?? "" };
        if (value is string && text.Length > 0 && "=+-@\t\r".Contains(text[0])) text = "'" + text;
        return text.IndexOfAny([',', '"', '\r', '\n']) < 0 ? text : "\"" + text.Replace("\"", "\"\"") + "\"";
    }
}
