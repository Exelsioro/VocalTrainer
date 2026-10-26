using System.IO;
using System.Collections.ObjectModel;
using VocalTrainer.Infrastructure;

namespace VocalTrainer.ViewModels;

public sealed class HotkeyRow : ObservableObject
{
    private ShortcutChord _chord;
    public string Id { get; }
    public string Title { get; }
    public string Scope { get; }
    public string ScopeLabel => Scope switch
    {
        "trainer" => "Тренажёр", "exercises" => "Упражнения", "ear" => "Слух", "range" => "Диапазон",
        "midi" => "MIDI", "global" => "Везде", _ => Scope
    };
    public ShortcutChord Chord { get => _chord; set { if (Set(ref _chord, value)) OnChanged(nameof(Gesture)); } }
    public string Gesture => Chord.Display;
    public HotkeyRow(ShortcutDefinition def, ShortcutChord chord)
    { Id = def.Id; Title = def.Title; Scope = def.Scope; _chord = chord; }
}

public sealed class HotkeysViewModel : ObservableObject
{
    private readonly ShortcutStore _store;
    private HotkeyRow? _selected;
    private bool _capturing;
    private string _status = "Выбери действие и нажми «Изменить» для записи сочетания.";
    private Dictionary<string, ShortcutChord> _bindings;
    public ObservableCollection<HotkeyRow> Rows { get; } = [];
    public HotkeyRow? Selected { get => _selected; set => Set(ref _selected, value); }
    public bool IsCapturing { get => _capturing; private set { if (Set(ref _capturing, value)) OnChanged(nameof(CaptureMessage)); } }
    public string CaptureMessage => IsCapturing ? "Нажми сочетание. Esc — отмена, Backspace — снять назначение." : "Сочетания действуют только в окне Vocal Trainer.";
    public string Status { get => _status; private set => Set(ref _status, value); }
    public RelayCommand CaptureCommand { get; }
    public RelayCommand DefaultsCommand { get; }

    public HotkeysViewModel(string path)
    {
        _store = new ShortcutStore(path);
        try { _bindings = _store.Load(); }
        catch (InvalidDataException ex)
        {
            _bindings = ShortcutCatalog.Defaults();
            Status = ex.Message + " Используются стандартные клавиши; исходный файл сохранён.";
        }
        foreach (var def in ShortcutCatalog.Definitions)
            Rows.Add(new HotkeyRow(def, _bindings[def.Id]));
        Selected = Rows.FirstOrDefault();
        CaptureCommand = new RelayCommand(() =>
        {
            if (Selected is null) return;
            IsCapturing = true;
            Status = "Ожидание нажатия для «" + Selected.Title + "»";
        }, () => Selected is not null);
        DefaultsCommand = new RelayCommand(RestoreDefaults);
    }

    public IReadOnlyDictionary<string, ShortcutChord> Bindings => _bindings;
    public void CancelCapture()
    {
        IsCapturing = false;
        Status = "Изменение сочетания отменено.";
    }
    public bool Assign(ShortcutChord chord)
    {
        if (!IsCapturing || Selected is null) return false;
        var candidate = new Dictionary<string, ShortcutChord>(_bindings) { [Selected.Id] = chord };
        if (ShortcutCatalog.Validate(candidate) is { } problem)
        {
            Status = problem + ". Выбери другое сочетание либо Esc для отмены.";
            return false;
        }
        try { _store.Save(candidate); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException)
        {
            IsCapturing = false;
            Status = "Не удалось сохранить settings.json: " + ex.Message;
            return false;
        }
        _bindings = candidate;
        Selected.Chord = chord;
        IsCapturing = false;
        Status = "Сохранено: " + Selected.Title + " — " + chord.Display;
        return true;
    }
    private void RestoreDefaults()
    {
        var defaults = ShortcutCatalog.Defaults();
        try { _store.Save(defaults); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException)
        {
            Status = "Не удалось вернуть настройки: " + ex.Message; return;
        }
        _bindings = defaults;
        foreach (var row in Rows) row.Chord = defaults[row.Id];
        IsCapturing = false;
        Status = "Стандартные сочетания восстановлены и сохранены.";
    }
}
