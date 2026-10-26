using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.IO;

namespace VocalTrainer;

/// <summary>Editable, portable v1 exercise document; no audio embedded.</summary>
public sealed class ExerciseDocument
{
    public int SchemaVersion { get; set; } = 1;
    public string Title { get; set; } = "";
    public string Description { get; set; } = "";
    public List<string>? Goals { get; set; }
    public List<string>? Tasks { get; set; }
    public List<ExerciseSection> Sections { get; set; } = [];
}

public sealed class ExerciseSection
{
    public string Title { get; set; } = "";
    public string? Description { get; set; }
    // Sequential stack: first entry is played first.
    public List<ExerciseNote> Notes { get; set; } = [];
}

public sealed class ExerciseNote
{
    public string Note { get; set; } = "C3";
    public double PreviewSeconds { get; set; } = 2;
    public double HoldSeconds { get; set; } = 3;
    // Pause from the end of the hold until the next note or section.
    public double TransitionSeconds { get; set; } = 1;
    // Optional scoring rules; absent values use the settings selected in the UI.
    public double? ToleranceCents { get; set; }
    public double? RequiredAccuracy { get; set; } // fraction, 0.1 .. 1.0
    public bool? AutoRepeatOnFail { get; set; }
    public int? MaxAttempts { get; set; }
}

public static class ExerciseIO
{
    public static JsonSerializerOptions Options { get; } = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        NumberHandling = JsonNumberHandling.Strict
    };

    // C3, F#4, G♯2, Bb3, Db4; scientific octave convention, 0-127 MIDI.
    private static readonly Regex NoteRegex = new(@"^([A-Ga-g])([#♯b♭]?)(-?\d+)$", RegexOptions.Compiled);

    public static int ParseMidi(string input)
    {
        Match match = NoteRegex.Match(input?.Trim() ?? "");
        if (!match.Success) throw new FormatException($"Некорректная нота '{input}'. Пример: C3, F#4, Bb3.");
        int natural = char.ToUpperInvariant(match.Groups[1].Value[0]) switch
        {
            'C' => 0, 'D' => 2, 'E' => 4, 'F' => 5, 'G' => 7, 'A' => 9, 'B' => 11,
            _ => throw new FormatException("Неверная нота")
        };
        int shift = match.Groups[2].Value switch { "#" or "♯" => 1, "b" or "♭" => -1, _ => 0 };
        if (!int.TryParse(match.Groups[3].Value, out int octave)) throw new FormatException("Неверная октава");
        long midi = ((long)octave + 1) * 12 + natural + shift;
        if (midi is < 0 or > 127) throw new FormatException($"Нота '{input}' вне диапазона MIDI 0–127.");
        return (int)midi;
    }

    public static ExerciseDocument Load(string path)
    {
        using var stream = File.OpenRead(path);
        var doc = JsonSerializer.Deserialize<ExerciseDocument>(stream, Options)
            ?? throw new FormatException("JSON не содержит упражнения.");
        Validate(doc);
        return doc;
    }

    public static void Validate(ExerciseDocument doc)
    {
        if (doc.SchemaVersion != 1) throw new FormatException("Поддерживается только schemaVersion = 1.");
        if (string.IsNullOrWhiteSpace(doc.Title) || doc.Title.Length > 160)
            throw new FormatException("Поле title обязательно (до 160 символов).");
        if (doc.Description is null || doc.Description.Length > 3000)
            throw new FormatException("Описание должно быть строкой до 3000 символов.");
        if (doc.Goals?.Any(x => x is null) == true || doc.Tasks?.Any(x => x is null) == true)
            throw new FormatException("Цели и задачи должны быть строками.");
        if (doc.Goals?.Count > 30 || doc.Tasks?.Count > 30) throw new FormatException("Слишком много целей или задач.");
        if (doc.Sections is null || doc.Sections.Count is 0 or > 60)
            throw new FormatException("Нужно 1–60 секций.");
        double totalDuration = 0;
        foreach (var section in doc.Sections)
        {
            if (section is null || string.IsNullOrWhiteSpace(section.Title) || section.Title.Length > 160)
                throw new FormatException("У каждой секции должно быть название (до 160 символов).");
            if (section.Notes is null || section.Notes.Count is 0 or > 100)
                throw new FormatException($"В секции '{section.Title}' нужно 1–100 нот.");
            foreach (var note in section.Notes)
            {
                if (note is null) throw new FormatException("Пустая нота в стеке.");
                ParseMidi(note.Note);
                if (!ValidSeconds(note.PreviewSeconds, 0.2, 30))
                    throw new FormatException($"{note.Note}: previewSeconds допустимо 0,2–30 с.");
                if (!ValidSeconds(note.HoldSeconds, 0.5, 60))
                    throw new FormatException($"{note.Note}: holdSeconds допустимо 0,5–60 с.");
                if (!ValidSeconds(note.TransitionSeconds, 0, 30))
                    throw new FormatException($"{note.Note}: transitionSeconds допустимо 0–30 с.");
                if (note.ToleranceCents is { } t && (!double.IsFinite(t) || t is < 10 or > 100))
                    throw new FormatException($"{note.Note}: toleranceCents допустимо 10–100.");
                if (note.RequiredAccuracy is { } a && (!double.IsFinite(a) || a is < 0.1 or > 1))
                    throw new FormatException($"{note.Note}: requiredAccuracy допустимо 0,1–1,0.");
                if (note.MaxAttempts is { } attempts && attempts is < 1 or > 8)
                    throw new FormatException($"{note.Note}: maxAttempts допустимо 1–8.");
                totalDuration += note.PreviewSeconds + note.HoldSeconds + note.TransitionSeconds;
            }
        }
        if (totalDuration > 3600) throw new FormatException("Длительность упражнения не должна превышать 60 минут.");
    }

    private static bool ValidSeconds(double value, double min, double max)
        => double.IsFinite(value) && value >= min && value <= max;
}
