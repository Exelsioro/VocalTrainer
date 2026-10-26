using System.IO;
using System.Collections.ObjectModel;
using System.Diagnostics;
using VocalTrainer.Infrastructure;
using VocalTrainer.Practice;

namespace VocalTrainer.ViewModels;

public sealed record ComfortOption(string Code, string Label)
{
    public override string ToString() => Label;
}

public sealed record RangeCheckRow(string Note, string MedianError, string Coverage, string Stability, string Comfort);

public sealed class RangeDiagnosticsViewModel : ObservableObject
{
    private readonly Func<bool> _available;
    private readonly Action<double, double> _playTone;
    private readonly Action _stopTone;
    private readonly Action<string, object> _log;
    private readonly List<RangeSample> _samples = [];
    private readonly RangeHistoryStore _history;
    private readonly Stopwatch _watch = new();
    private const double DurationSeconds = 3.5;
    private RangeMeasurementResult? _lastResult;
    private int _measurementMidi;
    private bool _isMeasuring;
    private string _status = "Вручную выбери ноту слева, прослушай её и затем проверь устойчивость голоса.";
    private string _progressText = "Измерение ещё не выполнено";
    private string _resultText = "Нет данных. Комфортность оценивается только тобой, не алгоритмом.";
    private ComfortOption? _selectedComfort;
    private StudyNoteOption? _selectedNote;
    public IReadOnlyList<StudyNoteOption> NoteChoices { get; } = StudyNoteOption.Make(36, 49);
    public StudyNoteOption? SelectedNote
    {
        get => _selectedNote;
        set { if (Set(ref _selectedNote, value)) Refresh(); }
    }
    public bool CanPreview => !IsMeasuring && _available() && SelectedNote is not null;
    public RelayCommand PreviewCommand { get; }

    public IReadOnlyList<ComfortOption> ComfortOptions { get; } =
    [
        new("easy", "Комфортно, без напряжения"),
        new("uncertain", "Неустойчиво / трудно"),
        new("strain", "Неприятные ощущения / напряжение")
    ];
    public ObservableCollection<RangeCheckRow> Checks { get; } = [];
    public bool IsMeasuring { get => _isMeasuring; private set { if (Set(ref _isMeasuring, value)) Refresh(); } }
    public bool CanStart => !IsMeasuring && _available() && SelectedNote is not null;
    public bool CanSave => !IsMeasuring && _lastResult is not null && SelectedComfort is not null;
    public string Status { get => _status; private set => Set(ref _status, value); }
    public string ProgressText { get => _progressText; private set => Set(ref _progressText, value); }
    public string ResultText { get => _resultText; private set => Set(ref _resultText, value); }
    public ComfortOption? SelectedComfort
    {
        get => _selectedComfort;
        set { if (Set(ref _selectedComfort, value)) Refresh(); }
    }
    public RelayCommand StartCheckCommand { get; }
    public RelayCommand SaveCheckCommand { get; }

    public RangeDiagnosticsViewModel(Func<bool> available, Action<double, double> playTone, Action stopTone,
        Action<string, object> log, string localStorage)
    {
        _available = available;
        _playTone = playTone;
        _stopTone = stopTone;
        _log = log;
        _history = new RangeHistoryStore(localStorage);
        foreach (var entry in _history.Load().Reverse()) Checks.Add(AsRow(entry));
        _selectedNote = NoteChoices.Single(x => x.Midi == 48);
        PreviewCommand = new RelayCommand(Preview, () => CanPreview);
        StartCheckCommand = new RelayCommand(Start, () => CanStart);
        SaveCheckCommand = new RelayCommand(Save, () => CanSave);
    }

    private void Preview()
    {
        if (!CanPreview || SelectedNote is null) return;
        try { _playTone(NoteCatalog.ToFrequency(SelectedNote.Midi), 2); }
        catch (Exception ex) { Status = "Ошибка эталона: " + ex.Message; }
    }

    public void Refresh()
    {
        OnChanged(nameof(CanStart)); OnChanged(nameof(CanSave)); OnChanged(nameof(CanPreview));
        StartCheckCommand.Refresh(); SaveCheckCommand.Refresh(); PreviewCommand.Refresh();
    }

    private void Start()
    {
        if (!CanStart) return;
        _stopTone();
        _measurementMidi = SelectedNote!.Midi;
        double hz = NoteCatalog.ToFrequency(_measurementMidi);
        if (hz < PitchDetector.MinHz || hz > PitchDetector.MaxHz)
        {
            Status = "Нота вне диапазона распознавания микрофона. Выбери другую.";
            return;
        }
        _samples.Clear();
        _lastResult = null;
        SelectedComfort = null;
        ResultText = "Идёт измерение...";
        ProgressText = "0 / 3,5 с";
        Status = $"Спой {NoteCatalog.ToBilingual(_measurementMidi)} спокойно. Без форсирования.";
        IsMeasuring = true;
        _watch.Restart();
        _log("range_start", new { midi = _measurementMidi, note = NoteCatalog.ToLatin(_measurementMidi) });
    }

    public void OnMeasurement(PitchMeasurement measurement)
    {
        if (!IsMeasuring) return;
        _samples.Add(new RangeSample(measurement.FrequencyHz, measurement.Confidence));
        ProgressText = $"{Math.Min(DurationSeconds, _watch.Elapsed.TotalSeconds):0.0} / {DurationSeconds:0.0} с";
        if (_watch.Elapsed.TotalSeconds < DurationSeconds) return;
        _watch.Stop();
        IsMeasuring = false;
        _lastResult = RangeMeasurementResult.Analyze(_measurementMidi, _samples);
        string median = _lastResult.MedianDeviationCents is { } c ? $"{c:+0;-0;0} ¢" : "—";
        string stability = _lastResult.MedianAbsoluteDeviationCents is { } deviation ? $"{deviation:0} ¢" : "—";
        ResultText = $"{NoteCatalog.ToLatin(_measurementMidi)} · медианная ошибка {median} · " +
            $"распознано {_lastResult.VoiceCoveragePercent:0}% · разброс {stability}. " + _lastResult.Conclusion;
        Status = "Теперь выбери субъективную оценку комфорта и сохрани результат.";
        _log("range_measured", _lastResult);
        Refresh();
    }

    private void Save()
    {
        if (!CanSave || _lastResult is null || SelectedComfort is null) return;
        string deviation = _lastResult.MedianDeviationCents is { } c ? $"{c:+0;-0;0} ¢" : "—";
        string stability = _lastResult.MedianAbsoluteDeviationCents is { } d ? $"{d:0} ¢" : "—";
        var entry = new RangeHistoryEntry(DateTimeOffset.UtcNow, _lastResult.Midi,
            _lastResult.VoiceCoveragePercent, _lastResult.MedianDeviationCents,
            _lastResult.MedianAbsoluteDeviationCents, SelectedComfort.Label);
        try { _history.Append(entry); }
        catch (IOException ex) { Status = "Не удалось сохранить карту диапазона: " + ex.Message; return; }
        Checks.Insert(0, AsRow(entry));
        _log("range_comfort", new { _lastResult.Midi, note = NoteCatalog.ToLatin(_lastResult.Midi),
            subjectiveComfort = SelectedComfort.Code, _lastResult.Accurate, _lastResult.MedianDeviationCents,
            _lastResult.MedianAbsoluteDeviationCents, _lastResult.VoiceCoveragePercent });
        _lastResult = null;
        SelectedComfort = null;
        Status = "Сохранено в журнал практики. Можно выбрать следующую ноту.";
        Refresh();
    }

    private static RangeCheckRow AsRow(RangeHistoryEntry entry) => new(
        NoteCatalog.ToBilingual(entry.Midi),
        entry.MedianCents is { } c ? $"{c:+0;-0;0} ¢" : "—",
        $"{entry.CoveragePercent:0}%",
        entry.StabilityCents is { } d ? $"{d:0} ¢" : "—",
        entry.Comfort);

    public void Cancel()
    {
        if (!IsMeasuring) return;
        _watch.Stop(); IsMeasuring = false;
        _samples.Clear();
        _log("range_cancel", new { midi = _measurementMidi });
        Status = "Замер прерван.";
        Refresh();
    }
}
