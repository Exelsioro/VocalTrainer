using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.ComponentModel;
using VocalTrainer.Infrastructure;
using VocalTrainer.Melody;

namespace VocalTrainer.ViewModels;

public sealed class LyricRow : ObservableObject
{
    private string _timeText, _text;
    private bool _isCurrent;
    public string TimeText { get => _timeText; set => Set(ref _timeText, value); }
    public string Text { get => _text; set => Set(ref _text, value); }
    public bool IsCurrent { get => _isCurrent; set => Set(ref _isCurrent, value); }
    public LyricRow(string time, string text) { _timeText = time; _text = text; }
}

public sealed class LyricsViewModel : ObservableObject
{
    private readonly Func<bool> _canEdit, _canMark;
    private readonly Func<double> _position, _trackEnd;
    private readonly Action<double, double> _selectFragment;
    private readonly Func<string?> _open, _save;
    private IReadOnlyList<LyricCue> _cues = [];
    private MidiLyricTrack? _selectedSource;
    private LyricRow? _selectedRow;
    private string _plainText = "", _current = "Добавь текст из MIDI, LRC или TXT", _next = "";
    private string _status = "Метки времени относятся к исходному MIDI; скорость и фрагмент учитываются автоматически.";
    private string _sourceName = "Текст не загружен";
    private bool _dirty, _external;
    private int _currentIndex = -1;
    public event Action<string?>? ContentChanged;

    public ObservableCollection<LyricRow> Rows { get; } = [];
    public ObservableCollection<MidiLyricTrack> Sources { get; } = [];
    public IReadOnlyList<LyricCue> Cues => _cues;
    public string CurrentLine { get => _current; private set => Set(ref _current, value); }
    public string NextLine { get => _next; private set => Set(ref _next, value); }
    public int CurrentIndex { get => _currentIndex; private set => Set(ref _currentIndex, value); }
    public string Status { get => _status; private set => Set(ref _status, value); }
    public string SourceName { get => _sourceName; private set => Set(ref _sourceName, value); }
    public string PlainText { get => _plainText; set { if (Set(ref _plainText, value)) ContentChanged?.Invoke(null); } }
    public bool HasUnappliedChanges { get => _dirty; private set => Set(ref _dirty, value); }
    public bool CanEdit => _canEdit();
    public bool IsReadOnly => !CanEdit;
    public LyricRow? SelectedRow { get => _selectedRow; set { if (Set(ref _selectedRow, value)) Refresh(); } }
    public MidiLyricTrack? SelectedSource
    {
        get => _selectedSource;
        set { if (CanEdit && Set(ref _selectedSource, value) && value is not null) UseMidi(); }
    }
    public RelayCommand ImportCommand { get; }
    public RelayCommand FromPlainTextCommand { get; }
    public RelayCommand ApplyCommand { get; }
    public RelayCommand MarkCommand { get; }
    public RelayCommand PracticeLineCommand { get; }
    public RelayCommand SaveCommand { get; }
    public RelayCommand UseMidiCommand { get; }
    public RelayCommand ClearCommand { get; }

    public LyricsViewModel(Func<bool> canEdit, Func<bool> canMark, Func<double> position, Func<double> trackEnd,
        Action<double, double> selectFragment, Func<string?> open, Func<string?> save)
    {
        _canEdit = canEdit; _canMark = canMark; _position = position; _trackEnd = trackEnd;
        _selectFragment = selectFragment; _open = open; _save = save;
        ImportCommand = new(Import, () => CanEdit);
        FromPlainTextCommand = new(() => LoadText(PlainText, false, "Введённый текст"), () => CanEdit);
        ApplyCommand = new(Apply, () => CanEdit && Rows.Count > 0);
        MarkCommand = new(Mark, () => _canMark() && SelectedRow is not null);
        PracticeLineCommand = new(PracticeLine, () => CanEdit && !_dirty && SelectedRow is { Text.Length: > 0 });
        SaveCommand = new(Save, () => CanEdit && !_dirty && _cues.Count > 0);
        UseMidiCommand = new(UseMidi, () => CanEdit && SelectedSource is not null);
        ClearCommand = new(Clear, () => CanEdit);
    }

    public void Refresh()
    {
        OnChanged(nameof(CanEdit)); OnChanged(nameof(IsReadOnly));
        ImportCommand.Refresh(); FromPlainTextCommand.Refresh(); ApplyCommand.Refresh(); MarkCommand.Refresh();
        PracticeLineCommand.Refresh(); SaveCommand.Refresh(); UseMidiCommand.Refresh(); ClearCommand.Refresh();
    }

    private void ReplaceRows(IEnumerable<LyricRow> rows)
    {
        foreach (var old in Rows) old.PropertyChanged -= OnRowChanged;
        Rows.Clear();
        foreach (var row in rows) { Rows.Add(row); row.PropertyChanged += OnRowChanged; }
        SelectedRow = Rows.FirstOrDefault();
    }

    private void OnRowChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not (nameof(LyricRow.TimeText) or nameof(LyricRow.Text))) return;
        HasUnappliedChanges = true;
        Status = "Есть изменения: заполни время строк и нажми «Применить разметку».";
        Highlight(_position()); Refresh();
        ContentChanged?.Invoke(null);
    }

    public void ResetMidiSources(IReadOnlyList<MidiLyricTrack> sources, int preferredTrack)
    {
        Clear(); Sources.Clear(); _selectedSource = null;
        foreach (var source in sources) Sources.Add(source);
        Set(ref _selectedSource, Sources.FirstOrDefault(s => s.TrackIndex == preferredTrack) ?? Sources.FirstOrDefault(), nameof(SelectedSource));
        if (_selectedSource is not null) UseMidi();
        Refresh();
    }

    public void OnTrackChanged(int track)
    {
        if (_external) return;
        var source = Sources.FirstOrDefault(s => s.TrackIndex == track) ?? Sources.FirstOrDefault();
        Set(ref _selectedSource, source, nameof(SelectedSource));
        if (source is not null) UseMidi();
    }

    private void UseMidi()
    {
        if (SelectedSource is not { } source) return;
        try { LoadCues(source.Lines, "MIDI · " + source.Title); _external = false; ContentChanged?.Invoke(null); }
        catch (Exception ex) { Status = "Текст MIDI не применён: " + ex.Message; }
    }

    private void LoadCues(IReadOnlyList<LyricCue> cues, string name)
    {
        LyricIO.Validate(cues);
        ReplaceRows(cues.Select(l => new LyricRow(l.Seconds.ToString("0.###", CultureInfo.InvariantCulture), l.Text)));
        _cues = cues.ToArray(); HasUnappliedChanges = false; SourceName = name;
        Status = $"Применено {cues.Count} строк/меток. Можно выбрать строку для практики.";
        Highlight(_position()); Refresh();
    }

    public void LoadText(string text, bool lrc, string name, string? sourcePath = null)
    {
        if (!CanEdit) return;
        try
        {
            if (lrc) LoadCues(LyricIO.ParseLrc(text), name);
            else
            {
                var lines = text.Replace("\r", "").Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToArray();
                if (text.Length > 1024 * 1024 || lines.Length is 0 or > 10000 || lines.Any(l => l.Length > 3000))
                    throw new FormatException("Нужен текст: 1–10000 строк, до 3000 символов на строку, всего до 1 МБ.");
                ReplaceRows(lines.Select(l => new LyricRow("", l)));
                _cues = []; HasUnappliedChanges = true; SourceName = name;
                Status = "У TXT нет таймкодов: задай время строк вручную или отмечай их во время прослушивания, затем примени разметку.";
                Highlight(_position()); Refresh();
            }
            _external = true;
            ContentChanged?.Invoke(sourcePath);
        }
        catch (Exception ex) { Status = "Текст не загружен: " + ex.Message; }
    }

    private void Import()
    {
        if (!CanEdit) return;
        try
        {
            string? path = _open();
            if (path is null) return;
            LoadText(LyricIO.ReadText(path), Path.GetExtension(path).Equals(".lrc", StringComparison.OrdinalIgnoreCase), Path.GetFileName(path), path);
        }
        catch (Exception ex) { Status = "Не удалось открыть текст: " + ex.Message; }
    }

    private void Apply()
    {
        if (!CanEdit) return;
        try { LoadCues(Rows.Select(r => new LyricCue(LyricIO.ParseTime(r.TimeText), r.Text.Trim())).ToArray(), SourceName); _external = true; ContentChanged?.Invoke(null); }
        catch (Exception ex) { Status = "Разметка не применена: " + ex.Message; }
    }

    private void Mark()
    {
        if (!_canMark() || SelectedRow is not { } row) return;
        int index = Rows.IndexOf(row);
        row.TimeText = _position().ToString("0.000", CultureInfo.InvariantCulture);
        if (index + 1 < Rows.Count) SelectedRow = Rows[index + 1];
    }

    private void PracticeLine()
    {
        if (!CanEdit || _dirty || SelectedRow is null) return;
        int index = Rows.IndexOf(SelectedRow);
        if (index < 0 || index >= _cues.Count) return;
        double from = _cues[index].Seconds, to = index + 1 < _cues.Count ? Math.Min(_cues[index + 1].Seconds, _trackEnd()) : _trackEnd();
        if (to <= from) { Status = "Эта строка вне длительности выбранной MIDI-дорожки."; return; }
        _selectFragment(from, to);
    }

    private void Save()
    {
        if (!CanEdit || _dirty || _cues.Count == 0) return;
        try
        {
            var path = _save();
            if (path is null) return;
            string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try { File.WriteAllText(temporary, LyricIO.ToLrc(_cues)); File.Move(temporary, path, true); }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
            Status = "Разметка сохранена: " + Path.GetFileName(path);
        }
        catch (Exception ex) { Status = "Не удалось сохранить LRC: " + ex.Message; }
    }

    public void Highlight(double sourceSeconds)
    {
        int index = LyricIO.At(_cues, sourceSeconds);
        if (CurrentIndex >= 0 && CurrentIndex < Rows.Count) Rows[CurrentIndex].IsCurrent = false;
        CurrentIndex = index;
        CurrentLine = index < 0 ? (_cues.Count == 0 ? "Добавь текст или примени разметку" : "♪ Вступление") :
            _cues[index].Text.Length == 0 ? "♪ Музыкальная пауза" : _cues[index].Text;
        NextLine = _cues.Skip(index + 1).FirstOrDefault(l => l.Text.Length > 0)?.Text ?? "";
        if (!_dirty && index >= 0 && index < Rows.Count) Rows[index].IsCurrent = true;
    }

    private void Clear()
    {
        ReplaceRows([]); _cues = []; _external = false; HasUnappliedChanges = false;
        Set(ref _plainText, "", nameof(PlainText));
        SourceName = "Текст не загружен"; Highlight(0); Refresh(); ContentChanged?.Invoke(null);
    }

    public SavedLyrics Snapshot() => new(SourceName, _external, SelectedSource?.TrackIndex, PlainText,
        HasUnappliedChanges, Rows.Select(r => new SavedLyricRow(r.TimeText, r.Text)).ToArray(), _cues.ToArray());

    public void Restore(SavedLyrics saved)
    {
        if (saved.Cues.Length > 0) LyricIO.Validate(saved.Cues);
        ReplaceRows(saved.Rows.Select(r => new LyricRow(r.Time, r.Text)));
        _cues = saved.Cues.ToArray(); _external = saved.External;
        Set(ref _plainText, saved.PlainText, nameof(PlainText));
        Set(ref _selectedSource, Sources.FirstOrDefault(s => s.TrackIndex == saved.SourceTrack), nameof(SelectedSource));
        SourceName = saved.SourceName; HasUnappliedChanges = saved.Dirty;
        Status = saved.Dirty ? "Черновик восстановлен. Заверши разметку и нажми «Применить разметку»." : "Сохранённый текст и разметка восстановлены.";
        Highlight(_position()); Refresh();
    }
}
