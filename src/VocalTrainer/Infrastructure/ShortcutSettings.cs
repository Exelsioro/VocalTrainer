using System.IO;
using System.Text.Json;

namespace VocalTrainer.Infrastructure;

// Platform-independent configuration/validation. Key names match WPF System.Windows.Input.Key.
public sealed record ShortcutChord(string Key, bool Ctrl = false, bool Shift = false, bool Alt = false)
{
    public bool IsEmpty => string.IsNullOrWhiteSpace(Key);
    public string Display => IsEmpty ? "Не назначено" :
        string.Join("+", new[] { Ctrl ? "Ctrl" : "", Shift ? "Shift" : "", Alt ? "Alt" : "", Key switch
        {
            "D1" => "1", "D2" => "2", "D3" => "3", "D4" => "4", "D5" => "5", "D6" => "6",
            "Return" => "Enter", "Escape" => "Esc", "Space" => "Space", _ => Key
        }}.Where(x => x.Length > 0));
}

public sealed record ShortcutDefinition(string Id, string Title, string Scope, ShortcutChord Default);

public static class ShortcutCatalog
{
    public static IReadOnlyList<ShortcutDefinition> Definitions { get; } =
    [
        new("trainer_toggle", "Старт / пауза / продолжить", "exercises", new("Space")),
        new("trainer_repeat", "Повторить текущую ноту", "exercises", new("R")),
        new("trainer_skip", "Пропустить ноту", "exercises", new("N")),
        new("trainer_repeat_tone", "Повторить эталонную ноту", "trainer", new("R")),
        new("trainer_preview", "Прослушать ноту", "trainer", new("Return")),
        new("ear_next", "Следующий слуховой тест", "ear", new("Space")),
        new("ear_higher", "Ответ: выше", "ear", new("D1")),
        new("ear_lower", "Ответ: ниже", "ear", new("D2")),
        new("ear_same", "Ответ: одинаково", "ear", new("D3")),
        new("ear_replay", "Повторить две ноты", "ear", new("Return")),
        new("range_check", "Измерить ноту", "range", new("Space")),
        new("midi_toggle", "MIDI: старт / пауза", "midi", new("Space")),
        new("midi_repeat", "MIDI: сначала фрагмента", "midi", new("R")),
        new("midi_preview", "MIDI: прослушать", "midi", new("Return")),
        new("stop", "Остановить упражнение / звук", "global", new("Escape")),
        new("mic", "Микрофон: вкл. / выкл.", "global", new("M", Ctrl:true)),
        new("record", "WAV: начать / остановить", "global", new("R", Ctrl:true, Shift:true)),
        new("import", "Импорт JSON или MIDI", "global", new("O", Ctrl:true)),
        new("save", "Сохранить JSON в редакторе", "global", new("S", Ctrl:true)),
        new("tab1", "Вкладка: тренажёр", "global", new("D1", Ctrl:true)),
        new("tab2", "Вкладка: упражнения", "global", new("D2", Ctrl:true)),
        new("tab3", "Вкладка: слух", "global", new("D3", Ctrl:true)),
        new("tab4", "Вкладка: диапазон", "global", new("D4", Ctrl:true)),
        new("tab5", "Вкладка: редактор", "global", new("D5", Ctrl:true)),
        new("tab6", "Вкладка: MIDI", "global", new("D6", Ctrl:true)),
        new("help", "Открыть настройки клавиш", "global", new("F1"))
    ];

    public static Dictionary<string, ShortcutChord> Defaults() =>
        Definitions.ToDictionary(d => d.Id, d => d.Default);

    public static bool Conflicts(string firstScope, string secondScope) =>
        firstScope == "global" || secondScope == "global" || firstScope == secondScope;

    public static string? Validate(IReadOnlyDictionary<string, ShortcutChord> chords)
    {
        foreach (var definition in Definitions)
        {
            if (!chords.TryGetValue(definition.Id, out var chord) || chord.IsEmpty) continue;
            if (chord.Key is "LWin" or "RWin" or "LeftCtrl" or "RightCtrl" or "LeftShift" or "RightShift" or "LeftAlt" or "RightAlt" or "System")
                return $"Недопустимая клавиша: {definition.Title}";
            if (chord.Alt && chord.Key == "F4") return "Alt+F4 зарезервировано системой";
            foreach (var other in Definitions)
            {
                if (definition.Id == other.Id || !Conflicts(definition.Scope, other.Scope)) continue;
                if (chords.TryGetValue(other.Id, out var otherChord) && chord == otherChord)
                    return $"Конфликт {chord.Display}: «{definition.Title}» и «{other.Title}»";
            }
        }
        return null;
    }

    public static string? Resolve(IReadOnlyDictionary<string, ShortcutChord> chords, ShortcutChord pressed, string scope)
    {
        return Definitions.FirstOrDefault(d =>
            (d.Scope == scope || d.Scope == "global") &&
            chords.TryGetValue(d.Id, out var gesture) && !gesture.IsEmpty && gesture == pressed)?.Id;
    }
}

public sealed class ShortcutStore(string path)
{
    private sealed class FileModel { public int SchemaVersion { get; set; } = 1; public Dictionary<string, ShortcutChord> Bindings { get; set; } = []; }
    private static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true, WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public Dictionary<string, ShortcutChord> Load()
    {
        var defaults = ShortcutCatalog.Defaults();
        if (!File.Exists(path)) return defaults;
        try
        {
            var file = JsonSerializer.Deserialize<FileModel>(File.ReadAllText(path), Options);
            if (file is null || file.SchemaVersion != 1 || file.Bindings is null) throw new FormatException("Неизвестная версия настроек");
            foreach (var def in ShortcutCatalog.Definitions)
                if (file.Bindings.TryGetValue(def.Id, out var binding) && binding is not null) defaults[def.Id] = binding;
            if (ShortcutCatalog.Validate(defaults) is { } error) throw new FormatException(error);
            return defaults;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or FormatException)
        {
            // Preserve a corrupted file for recovery, never silently overwrite it.
            throw new InvalidDataException("Не удалось загрузить settings.json: " + ex.Message, ex);
        }
    }

    public void Save(IReadOnlyDictionary<string, ShortcutChord> bindings)
    {
        if (ShortcutCatalog.Validate(bindings) is { } error) throw new FormatException(error);
        var directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);
        var temp = path + ".tmp";
        try
        {
            File.WriteAllText(temp, JsonSerializer.Serialize(new FileModel
            {
                Bindings = bindings.ToDictionary(kv => kv.Key, kv => kv.Value)
            }, Options));
            File.Move(temp, path, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}
