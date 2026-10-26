using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using VocalTrainer.Controls;
using VocalTrainer.Infrastructure;
using VocalTrainer.Practice;
using VocalTrainer.Services;

namespace VocalTrainer.ViewModels;

public sealed record NoteOption(int Index, string Latin, string Russian)
{
    public string Display => $"{Latin}   ·   {Russian}";
}
public sealed record FeedbackModeOption(string Code, string Title)
{
    public override string ToString() => Title;
}

/// <summary>Presentation state and commands; no WPF controls are referenced.</summary>
public sealed class MainViewModel : ObservableObject, IDisposable
{
    private readonly AudioService _audio;
    private readonly IDialogService _dialogs;
    private readonly ExerciseLibrary _exerciseLibrary = new(Path.Combine(AppDataPaths.Root, "exercise-library.json"));
    private ExerciseLibraryEntry? _selectedExercise;
    public ObservableCollection<ExerciseLibraryEntry> ExerciseChoices => _exerciseLibrary.Entries;
    public bool CanChooseExercise => !ExerciseActive;
    public ExerciseLibraryEntry? SelectedExercise
    {
        get => _selectedExercise;
        set
        {
            if (ExerciseActive || !Set(ref _selectedExercise, value) || value is null) return;
            ActivateExercise(value.Document, value.SourcePath);
        }
    }
    private readonly DispatcherTimer _uiTimer = new() { Interval = TimeSpan.FromMilliseconds(120) };
    private readonly Queue<PitchPoint> _history = new();
    private SessionLogger? _log;
    private bool _freeSingingEnabled;
    private long _freeStartedMs;
    public FreeSingingViewModel FreeSinging { get; } = new();
    public bool FreeSingingEnabled { get => _freeSingingEnabled; set => SetFreeSingingMode(value); }
    public bool CanChangeSingingMode => !ExerciseActive && !Melody.IsBusy && !RangeDiagnostics.IsMeasuring && !EarTraining.IsPlaying && _calibrationStartedUtc is null;
    public string TunerReferenceLabel => FreeSingingEnabled ? "БЛИЖАЙШАЯ НОТА" : "ЦЕЛЬ";
    public string TunerReferenceNote => FreeSingingEnabled ? FreeSinging.Note : NoteHeading;
    public string TunerReferenceDescription => FreeSingingEnabled ? FreeSinging.Octave : NoteSubheading;
    public string TunerReferenceFrequency => FreeSingingEnabled ?
        (FreeSinging.Current.Midi is { } midi ? $"{NoteCatalog.ToFrequency(midi):0.00} Гц · ближайшая нота" : "Ожидание голоса") : TargetHzText;
    public string IntonationGraphTitle => FreeSingingEnabled ? "Точность относительно ближайшей ноты" : "Отклонение от эталона";
    private readonly PracticeEventLog _studyLog;
    public EarTrainingViewModel EarTraining { get; }
    public RangeDiagnosticsViewModel RangeDiagnostics { get; }
    public ExerciseEditorViewModel ExerciseEditor { get; }
    public MelodyViewModel Melody { get; }
    public HotkeysViewModel Hotkeys { get; }
    private int _selectedTabIndex;
    public int SelectedTabIndex { get => _selectedTabIndex; set { if (Set(ref _selectedTabIndex, value) && value == 1 && FreeSingingEnabled) SetFreeSingingMode(false, true); } }
    private ExerciseDocument? _exercise;
    private CancellationTokenSource? _exerciseCancel;
    private PausableClock? _exerciseClock;
    private bool _exercisePaused;
    private bool _repeatRequested;
    private bool _phasePreview;
    private double _phaseStartSeconds, _phaseSeconds;
    private string _unpausedPhase = "";
    private long _holdStartedLogicalMs;
    private bool _disposed;
    private bool _syncSelection;
    private bool _stoppingSession;
    private int _targetMidi = 48;
    private NoteOption? _selectedNote;
    private int _selectedOctave = 3;
    private MicrophoneOption? _selectedMicrophone;
    private string _durationText = "2,5";
    private string _noteHeading = "C3";
    private string _noteSubheading = "До (малая октава)";
    private string _targetHzText = "130,81 Гц";
    private string _detectedNoteText = "—";
    private string _detectedHzText = "Нет устойчивого тона";
    private string _deviationText = "— ¢";
    private string _accuracyText = "Ожидание микрофона";
    private Brush _accuracyBrush = MakeBrush("#94A3BF");
    private double _tunerCents = double.NaN;
    private IReadOnlyList<PitchPoint> _historyPoints = [];
    private string _confidenceText = "Достоверность: —";
    private string _rangeWarningText = "";
    private string _statusText = "Готово к работе. Выбери ноту или загрузи упражнение.";
    private string _sessionStateText = "НЕ АКТИВНО";
    private string _sessionButtonText = "Начать занятие";
    private string _recordButtonText = "Записать WAV";
    private string _recordingStatusText = "Голос не сохраняется автоматически";
    private string _summaryText = "После занятия здесь появится статистика точности.";
    private string _exerciseTitle = "Упражнение не выбрано";
    private string _exerciseDescription = "Импортируй JSON с секциями и нотами или используй пример из комплекта.";
    private string _exerciseGoals = "";
    private string _exerciseTasks = "";
    private string _exerciseMeta = "—";
    private string _exercisePhase = "ГОТОВО";
    private string _exercisePosition = "Пока нет активного упражнения";
    private string _exerciseCountdown = "—";
    private string _sectionTitle = "Шкала упражнения";
    private double _exerciseProgress;
    private string _trainingNote = "";
    private string _calibrationText = "Шумовой порог: 0,008 RMS · не калиброван";
    private string _latestAssessmentText = "Оценка следующей попытки появится здесь.";
    private double _noiseGateRms = PitchDetector.DefaultGateRms;
    private DateTime? _calibrationStartedUtc;
    private readonly List<double> _calibrationRms = [];
    private AttemptAssessment? _attemptAssessment;
    private FeedbackModeOption? _selectedFeedbackMode;
    private int _selectedTolerance = 40;
    private int _selectedMaxAttempts = 3;
    private bool _autoRepeatFailed = true;
    private bool _feedbackHidden;
    private bool _skipRequested;
    private int _successfulSteps, _evaluatedSteps;
    private readonly List<AssessmentResult> _exerciseResults = [];

    public IReadOnlyList<NoteOption> Notes { get; } = NoteCatalog.Names
        .Select((x, index) => new NoteOption(index, x.Latin, x.Russian)).ToArray();
    public IReadOnlyList<OctaveOption> Octaves { get; } = Enumerable.Range(-1, 11)
        .Select(octave => new OctaveOption(octave, NoteCatalog.ToOctaveName(octave))).ToArray();
    public IReadOnlyList<FeedbackModeOption> FeedbackModes { get; } =
    [
        new("live", "С подсказкой · онлайн"),
        new("ear", "На слух · результат после ноты"),
        new("test", "Контроль · результат после упражнения")
    ];
    public IReadOnlyList<int> ToleranceOptions { get; } = [20, 25, 30, 40, 50, 75, 100];
    public IReadOnlyList<int> AttemptOptions { get; } = [1, 2, 3, 4, 5, 6, 7, 8];
    public ObservableCollection<MicrophoneOption> Microphones { get; } = [];
    public ObservableCollection<TimelineStep> Timeline { get; } = [];

    public NoteOption? SelectedNote
    {
        get => _selectedNote;
        set { if (Set(ref _selectedNote, value)) UpdateManualSelection(); }
    }
    public int SelectedOctave
    {
        get => _selectedOctave;
        set { if (Set(ref _selectedOctave, value)) UpdateManualSelection(); }
    }
    public MicrophoneOption? SelectedMicrophone
    {
        get => _selectedMicrophone;
        set => Set(ref _selectedMicrophone, value);
    }
    public string DurationText { get => _durationText; set => Set(ref _durationText, value); }
    public string NoteHeading { get => _noteHeading; private set => Set(ref _noteHeading, value); }
    public string NoteSubheading { get => _noteSubheading; private set => Set(ref _noteSubheading, value); }
    public string TargetHzText { get => _targetHzText; private set => Set(ref _targetHzText, value); }
    public string DetectedNoteText { get => _detectedNoteText; private set => Set(ref _detectedNoteText, value); }
    public string DetectedHzText { get => _detectedHzText; private set => Set(ref _detectedHzText, value); }
    public string DeviationText { get => _deviationText; private set => Set(ref _deviationText, value); }
    public string AccuracyText { get => _accuracyText; private set => Set(ref _accuracyText, value); }
    public Brush AccuracyBrush { get => _accuracyBrush; private set => Set(ref _accuracyBrush, value); }
    public double TunerCents { get => _tunerCents; private set => Set(ref _tunerCents, value); }
    public IReadOnlyList<PitchPoint> HistoryPoints { get => _historyPoints; private set => Set(ref _historyPoints, value); }
    public string ConfidenceText { get => _confidenceText; private set => Set(ref _confidenceText, value); }
    public string RangeWarningText { get => _rangeWarningText; private set => Set(ref _rangeWarningText, value); }
    public string StatusText { get => _statusText; private set => Set(ref _statusText, value); }
    public string SessionStateText { get => _sessionStateText; private set => Set(ref _sessionStateText, value); }
    public string SessionButtonText { get => _sessionButtonText; private set => Set(ref _sessionButtonText, value); }
    public string RecordButtonText { get => _recordButtonText; private set => Set(ref _recordButtonText, value); }
    public string RecordingStatusText { get => _recordingStatusText; private set => Set(ref _recordingStatusText, value); }
    public string SummaryText { get => _summaryText; private set => Set(ref _summaryText, value); }
    public string ExerciseTitle { get => _exerciseTitle; private set => Set(ref _exerciseTitle, value); }
    public string ExerciseDescription { get => _exerciseDescription; private set => Set(ref _exerciseDescription, value); }
    public string ExerciseGoals { get => _exerciseGoals; private set => Set(ref _exerciseGoals, value); }
    public string ExerciseTasks { get => _exerciseTasks; private set => Set(ref _exerciseTasks, value); }
    public string ExerciseMeta { get => _exerciseMeta; private set => Set(ref _exerciseMeta, value); }
    public string ExercisePhase { get => _exercisePhase; private set => Set(ref _exercisePhase, value); }
    public string ExercisePosition { get => _exercisePosition; private set => Set(ref _exercisePosition, value); }
    public string ExerciseCountdown { get => _exerciseCountdown; private set => Set(ref _exerciseCountdown, value); }
    public string SectionTitle { get => _sectionTitle; private set => Set(ref _sectionTitle, value); }
    public double ExerciseProgress { get => _exerciseProgress; private set => Set(ref _exerciseProgress, value); }
    public string TrainingNote { get => _trainingNote; set => Set(ref _trainingNote, value); }
    public string CalibrationText { get => _calibrationText; private set => Set(ref _calibrationText, value); }
    public string LatestAssessmentText { get => _latestAssessmentText; private set => Set(ref _latestAssessmentText, value); }
    public FeedbackModeOption? SelectedFeedbackMode
    {
        get => _selectedFeedbackMode;
        set { if (Set(ref _selectedFeedbackMode, value)) { _log?.Event("feedback_mode_change", new { mode = value?.Code }); RefreshFeedbackVisibility(); } }
    }
    public int SelectedTolerance { get => _selectedTolerance; set => Set(ref _selectedTolerance, value); }
    public int SelectedMaxAttempts { get => _selectedMaxAttempts; set => Set(ref _selectedMaxAttempts, value); }
    public bool AutoRepeatFailed { get => _autoRepeatFailed; set => Set(ref _autoRepeatFailed, value); }
    public bool FeedbackHidden { get => _feedbackHidden; private set => Set(ref _feedbackHidden, value); }
    public string FeedbackStatus => FeedbackHidden ? "Результат скрыт до завершения попытки" : "Высота голоса отображается онлайн";

    public bool SessionActive => _audio.IsMonitoring && !_stoppingSession;
    public bool HasExercise => _exercise is not null;
    public bool ExerciseActive => _exerciseCancel is not null;
    public bool ExercisePaused => _exercisePaused;
    public bool CanPauseExercise => ExerciseActive;
    public string ExercisePauseButtonText => _exercisePaused ? "▶ Продолжить [Space]" : "Ⅱ Пауза [Space]";
    public bool CanChangeNote => !ExerciseActive && !RangeDiagnostics.IsMeasuring && !FreeSingingEnabled;
    public bool CanChooseMicrophone => !SessionActive && !_stoppingSession;
    public bool CanRecord => SessionActive;
    public bool CanPlayTone => !ExerciseActive && !Melody.IsBusy && !EarTraining.IsPlaying && !RangeDiagnostics.IsMeasuring && _calibrationStartedUtc is null;
    public bool CanStartExercise => HasExercise && SessionActive && !ExerciseActive && !Melody.IsBusy && !EarTraining.IsPlaying && !RangeDiagnostics.IsMeasuring && _calibrationStartedUtc is null;
    public bool CanStopExercise => ExerciseActive;
    public bool CanCalibrate => SessionActive && !ExerciseActive && !Melody.IsBusy && !EarTraining.IsPlaying && !RangeDiagnostics.IsMeasuring && _calibrationStartedUtc is null;
    public bool CanSkipStep => ExerciseActive;

    public RelayCommand PlayToneCommand { get; }
    public RelayCommand StopToneCommand { get; }
    public RelayCommand ToggleSessionCommand { get; }
    public RelayCommand ToggleRecordingCommand { get; }
    public RelayCommand ImportExerciseCommand { get; }
    public RelayCommand StartExerciseCommand { get; }
    public RelayCommand StopExerciseCommand { get; }
    public RelayCommand SaveNoteCommand { get; }
    public RelayCommand OpenFolderCommand { get; }
    public RelayCommand CalibrateCommand { get; }
    public RelayCommand SkipStepCommand { get; }
    public RelayCommand ToggleExercisePauseCommand { get; }
    public RelayCommand RepeatStepCommand { get; }

    public MainViewModel(AudioService audio, IDialogService dialogs)
    {
        _audio = audio;
        _dialogs = dialogs;
        _studyLog = new PracticeEventLog(AppDataPaths.Logs);
        Hotkeys = new HotkeysViewModel(Path.Combine(AppDataPaths.Root, "settings.json"));
        RangeDiagnostics = new RangeDiagnosticsViewModel(
            CanUseRangeDiagnostics,
            _audio.PlayTone, _audio.StopTone, LogPractice, AppDataPaths.Root);
        EarTraining = new EarTrainingViewModel(_audio.PlayTone, _audio.StopTone,
            LogPractice, CanUseEarTraining);
        Melody = new MelodyViewModel(_audio.PlayTone, _audio.StopTone,
            () => !ExerciseActive && !EarTraining.IsPlaying && !RangeDiagnostics.IsMeasuring && _calibrationStartedUtc is null,
            () => SessionActive, _dialogs.SelectMidi, LogPractice, ShowError, _dialogs.SelectLyrics, _dialogs.SaveLyrics,
            new VocalTrainer.Melody.SongLibrary(AppDataPaths.Songs));
        Melody.PropertyChanged += OnStudyStateChanged;
        ExerciseEditor = new ExerciseEditorViewModel(_dialogs, SetExercise,
            () => !ExerciseActive, LogPractice);
        EarTraining.PropertyChanged += OnStudyStateChanged;
        RangeDiagnostics.PropertyChanged += OnStudyStateChanged;
        _audio.MonitoringStopped += OnMonitoringStopped;
        PlayToneCommand = new RelayCommand(PlayTone, () => CanPlayTone);
        StopToneCommand = new RelayCommand(_audio.StopTone);
        ToggleSessionCommand = new RelayCommand(ToggleSession, () => !_stoppingSession);
        ToggleRecordingCommand = new RelayCommand(ToggleRecording, () => CanRecord);
        ImportExerciseCommand = new RelayCommand(ImportExercise, () => !ExerciseActive);
        StartExerciseCommand = new RelayCommand(() => _ = RunExerciseAsync(), () => CanStartExercise);
        StopExerciseCommand = new RelayCommand(CancelExercise, () => CanStopExercise);
        SaveNoteCommand = new RelayCommand(SaveNote);
        OpenFolderCommand = new RelayCommand(() => _dialogs.OpenDataFolder(AppDataPaths.Root));
        CalibrateCommand = new RelayCommand(StartCalibration, () => CanCalibrate);
        SkipStepCommand = new RelayCommand(RequestSkip, () => CanSkipStep);
        ToggleExercisePauseCommand = new RelayCommand(ToggleExercisePause, () => CanPauseExercise);
        RepeatStepCommand = new RelayCommand(RequestRepeat, () => ExerciseActive);
        SelectedFeedbackMode = FeedbackModes[0];
        _syncSelection = true;
        SelectedNote = Notes[0];
        SelectedOctave = 3;
        _syncSelection = false;
        SetTarget(48, true);
        _uiTimer.Tick += OnTick;
        try
        {
            foreach (var device in AudioService.FindMicrophones()) Microphones.Add(device);
            SelectedMicrophone = Microphones.FirstOrDefault();
            if (SelectedMicrophone is null) StatusText = "Нет микрофонов. Проверь настройки доступа Windows.";
        }
        catch (Exception ex) { StatusText = "Не удалось получить список микрофонов: " + ex.Message; }

        _exerciseLibrary.Load(Path.Combine(AppContext.BaseDirectory, "exercises"), Path.Combine(AppDataPaths.Root, "Exercises"));
        SelectedExercise = ExerciseChoices.FirstOrDefault(e => Path.GetFileName(e.SourcePath).Equals("first-notes.json", StringComparison.OrdinalIgnoreCase)) ?? ExerciseChoices.FirstOrDefault();
        if (_exerciseLibrary.Warning is { } warning) StatusText = warning;
    }

    private bool CanUseRangeDiagnostics() => SessionActive && !ExerciseActive &&
        _calibrationStartedUtc is null && !EarTraining.IsPlaying && !Melody.IsBusy;

    private bool CanUseEarTraining() => !ExerciseActive && !RangeDiagnostics.IsMeasuring &&
        !Melody.IsBusy && _calibrationStartedUtc is null;

    private static Brush MakeBrush(string hex)
    {
        var brush = (SolidColorBrush)new BrushConverter().ConvertFrom(hex)!;
        brush.Freeze();
        return brush;
    }

    private void LogPractice(string kind, object payload)
    {
        try { _studyLog.Write(kind, payload); }
        catch (IOException ex) { StatusText = "Не удалось сохранить учебный лог: " + ex.Message; }
        _log?.Event("study_" + kind, payload);
    }

    private void OnStudyStateChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(EarTrainingViewModel.IsPlaying) or nameof(RangeDiagnosticsViewModel.IsMeasuring) or nameof(MelodyViewModel.IsBusy))
        {
            if (FreeSingingEnabled && (Melody.IsBusy || RangeDiagnostics.IsMeasuring || EarTraining.IsPlaying)) SetFreeSingingMode(false, true);
            RefreshCommands();
            EarTraining.Refresh();
            RangeDiagnostics.Refresh();
            ExerciseEditor.Refresh();
        }
    }

    private void RefreshCommands()
    {
        foreach (string property in new[]
        {
            nameof(SessionActive), nameof(HasExercise), nameof(ExerciseActive),
            nameof(CanChangeNote), nameof(CanChooseMicrophone), nameof(CanRecord), nameof(CanChooseExercise),
            nameof(CanPlayTone), nameof(CanStartExercise), nameof(CanStopExercise),
            nameof(CanCalibrate), nameof(CanSkipStep), nameof(CanPauseExercise),
            nameof(ExercisePaused), nameof(ExercisePauseButtonText)
        }) OnChanged(property);
        OnChanged(nameof(CanChangeSingingMode)); RefreshTunerReference();
        PlayToneCommand.Refresh();
        ImportExerciseCommand.Refresh();
        ToggleSessionCommand.Refresh();
        ToggleRecordingCommand.Refresh();
        StartExerciseCommand.Refresh();
        StopExerciseCommand.Refresh();
        CalibrateCommand.Refresh();
        SkipStepCommand.Refresh();
        ToggleExercisePauseCommand.Refresh();
        RepeatStepCommand.Refresh();
        EarTraining.Refresh();
        RangeDiagnostics.Refresh();
        ExerciseEditor.Refresh();
        Melody.Refresh();
    }

    private void UpdateManualSelection()
    {
        if (_syncSelection || ExerciseActive || SelectedNote is null) return;
        int midi = NoteCatalog.ToMidi(SelectedNote.Index, SelectedOctave);
        if (midi is < 0 or > 127)
        {
            RangeWarningText = "Комбинация выходит за пределы MIDI 0–127. Выбери другую ноту.";
            return;
        }
        SetTarget(midi, false);
    }

    private void SetTarget(int midi, bool updateSelectors)
    {
        if (midi is < 0 or > 127) return;
        _targetMidi = midi;
        if (updateSelectors)
        {
            _syncSelection = true;
            SelectedNote = Notes[midi % 12];
            SelectedOctave = midi / 12 - 1;
            _syncSelection = false;
        }
        NoteHeading = NoteCatalog.ToLatin(midi);
        NoteSubheading = NoteCatalog.ToRussian(midi);
        TargetHzText = $"{NoteCatalog.ToFrequency(midi):F2} Гц  ·  MIDI {midi}";
        RangeWarningText = NoteCatalog.ToFrequency(midi) < PitchDetector.MinHz ||
                           NoteCatalog.ToFrequency(midi) > PitchDetector.MaxHz
            ? $"Нота воспроизводится, но определение голоса ограничено {PitchDetector.MinHz:0}–{PitchDetector.MaxHz:0} Гц."
            : "";
        _history.Clear();
        HistoryPoints = [];
        TunerCents = double.NaN;
        _log?.TargetChanged(midi);
        RefreshTunerReference();
    }

    private void RefreshTunerReference()
    {
        foreach (string property in new[] { nameof(TunerReferenceLabel), nameof(TunerReferenceNote), nameof(TunerReferenceDescription),
                     nameof(TunerReferenceFrequency), nameof(IntonationGraphTitle) }) OnChanged(property);
    }

    private void SetFreeSingingMode(bool enabled, bool force = false)
    {
        if ((!force && !CanChangeSingingMode) || !Set(ref _freeSingingEnabled, enabled, nameof(FreeSingingEnabled))) return;
        if (enabled) StartFreeSegment();
        else { _log?.Event("free_singing_end", FreeSinging.Statistics); _log?.SetPhase("manual"); }
        _history.Clear(); HistoryPoints = []; TunerCents = double.NaN;
        RefreshCommands();
    }

    private void StartFreeSegment()
    {
        FreeSinging.Reset(); _freeStartedMs = _log?.ElapsedMs ?? 0;
        _log?.SetPhase("free_singing");
        _log?.Event("free_singing_start", new { nearestNoteAccuracyCents = 25, audioRecordedAutomatically = false });
    }

    private void PlayTone()
    {
        if (!double.TryParse(DurationText.Replace('.', ','), NumberStyles.Float,
                CultureInfo.GetCultureInfo("ru-RU"), out double duration) ||
            !double.IsFinite(duration) || duration is < .2 or > 30)
        {
            _dialogs.Info("Длительность", "Укажи длительность от 0,2 до 30 секунд.");
            return;
        }
        try { _audio.PlayTone(NoteCatalog.ToFrequency(_targetMidi), duration); StatusText = $"Звучит {NoteHeading} · {duration:F1} с"; }
        catch (Exception ex) { _audio.StopTone(); ShowError("Воспроизведение", ex); }
    }

    private void ToggleSession()
    {
        if (_audio.IsMonitoring)
        {
            CancelExercise();
            Melody.StopForMicrophone();
            _stoppingSession = true;
            _uiTimer.Stop();
            RefreshCommands();
            try { _audio.StopMonitoring(); }
            catch (Exception ex) { _stoppingSession = false; ShowError("Остановка микрофона", ex); RefreshCommands(); }
            return;
        }
        if (SelectedMicrophone is not { } microphone)
        {
            _dialogs.Info("Микрофон", "Выбери устройство ввода в левой панели.");
            return;
        }
        try
        {
            _audio.StartMonitoring(microphone.Id);
            try { _log = new SessionLogger(AppDataPaths.Logs, _targetMidi, microphone.Name); }
            catch { _audio.StopMonitoring(); throw; }
            _uiTimer.Start();
            _noiseGateRms = PitchDetector.DefaultGateRms;
            CalibrationText = "Шумовой порог: 0,008 RMS · запусти калибровку в тишине";
            _log?.Event("training_configuration", new { mode = SelectedFeedbackMode?.Code, SelectedTolerance,
                SelectedMaxAttempts, AutoRepeatFailed, minimumConfidence = 0.65 });
            if (FreeSingingEnabled) StartFreeSegment();
            SessionStateText = "МИКРОФОН АКТИВЕН";
            SessionButtonText = "Завершить занятие";
            SummaryText = "Идёт анализ в памяти. WAV-файлы не создаются.";
            StatusText = "Микрофон включён. Измерения ~8 раз/с, результаты сохраняются в JSONL.";
        }
        catch (Exception ex) { ShowError("Включение микрофона", ex); }
        RefreshCommands();
    }

    private void OnMonitoringStopped(object? sender, Exception? error)
    {
        // NAudio callbacks do not necessarily run on the UI thread.
        if (_disposed) return;
        _ = Application.Current.Dispatcher.BeginInvoke(new Action(() => FinishSession(error)));
    }

    private void FinishSession(Exception? error)
    {
        if (_disposed) return;
        CancelExercise();
        Melody.StopForMicrophone();
        _calibrationStartedUtc = null;
        _uiTimer.Stop();
        RangeDiagnostics.Cancel();
        var finishedRecording = _audio.FinishRecording() ?? _audio.TakeAutomaticallyFinishedRecording();
        if (finishedRecording is not null) _log?.Event("recording_stopped", new { fileName = Path.GetFileName(finishedRecording) });
        RecordButtonText = "Записать WAV";
        RecordingStatusText = finishedRecording is null ? "Запись выключена" : $"Сохранено: {Path.GetFileName(finishedRecording)}";
        if (FreeSingingEnabled) _log?.Event("free_singing_end", FreeSinging.Statistics);
        SummaryText = _log?.Finish() ?? "Занятие завершено";
        if (_log is not null) StatusText = "Лог: " + _log.FilePath;
        _log = null;
        _stoppingSession = false;
        SessionStateText = "НЕ АКТИВНО";
        SessionButtonText = "Начать занятие";
        DetectedNoteText = "—";
        DetectedHzText = "Нет активного микрофона";
        DeviationText = "— ¢";
        TunerCents = double.NaN;
        if (error is not null) StatusText = "Ошибка микрофона: " + error.Message;
        RefreshFeedbackVisibility();
        RefreshCommands();
    }

    private void ToggleRecording()
    {
        try
        {
            if (_audio.IsRecording)
            {
                string? path = _audio.FinishRecording();
                _log?.Event("recording_stopped", new { fileName = Path.GetFileName(path) });
                RecordButtonText = "Записать WAV";
                RecordingStatusText = $"Сохранено: {Path.GetFileName(path)}";
            }
            else
            {
                string path = _audio.StartRecording(AppDataPaths.Recordings);
                _log?.Event("recording_started", new { fileName = Path.GetFileName(path) });
                RecordButtonText = "Остановить WAV";
                RecordingStatusText = "Идёт отдельная запись в WAV";
            }
        }
        catch (Exception ex) { ShowError("Запись WAV", ex); }
    }

    private void StartCalibration()
    {
        _calibrationRms.Clear();
        _calibrationStartedUtc = DateTime.UtcNow;
        CalibrationText = "Калибровка 3 с: молчи, не воспроизводи звук...";
        _audio.StopTone();
        _log?.Event("calibration_start");
        RefreshCommands();
    }

    private void CompleteCalibration()
    {
        _calibrationStartedUtc = null;
        if (_calibrationRms.Count < 10)
        {
            CalibrationText = "Калибровка не удалась: мало данных. Повтори.";
            _log?.Event("calibration_failed", new { reason = "insufficient_frames" });
        }
        else
        {
            double[] sorted = _calibrationRms.Order().ToArray();
            double baseline = sorted[(int)((sorted.Length - 1) * .75)];
            if (baseline > .035)
            {
                CalibrationText = $"Слишком шумно (RMS {baseline:0.000}): убери шум и повтори.";
                _log?.Event("calibration_failed", new { reason = "high_noise", baselineRms = baseline });
            }
            else
            {
                _noiseGateRms = Math.Max(PitchDetector.DefaultGateRms, baseline * 3.5);
                CalibrationText = $"Калибровано: шум {baseline:0.0000} · порог {_noiseGateRms:0.0000} RMS";
                _log?.Event("calibration_complete", new { noiseFloorRms = baseline, gateRms = _noiseGateRms, samples = sorted.Length });
            }
        }
        RefreshCommands();
    }

    private void RefreshFeedbackVisibility()
    {
        // The exercise's target note remains visible. Only live microphone feedback is hidden.
        FeedbackHidden = ExerciseActive && ((SelectedFeedbackMode?.Code == "test") ||
            (SelectedFeedbackMode?.Code == "ear" && ExercisePhase == "ПОЙ И УДЕРЖИВАЙ"));
        OnChanged(nameof(FeedbackStatus));
        if (FeedbackHidden)
        {
            DetectedNoteText = "—";
            DetectedHzText = "Подсказки скрыты";
            DeviationText = "—";
            AccuracyText = "Слушай себя";
            ConfidenceText = "Оценка будет показана позже";
            TunerCents = double.NaN;
            HistoryPoints = [];
        }
    }

    private void OnTick(object? sender, EventArgs args)
    {
        if (!_audio.IsMonitoring) return;
        PitchMeasurement result = _audio.ReadPitch(_noiseGateRms);
        if (_calibrationStartedUtc is { } started)
        {
            if (result.Rms > 0) _calibrationRms.Add(result.Rms);
            double elapsed = (DateTime.UtcNow - started).TotalSeconds;
            CalibrationText = $"Калибровка: молчи ещё {Math.Max(0, 3 - elapsed):0.0} с";
            if (elapsed >= 3) CompleteCalibration();
            return;
        }
        if (_exercisePaused || Melody.IsPaused) return; // do not score or plot paused wall time
        ApplyPitchMeasurement(result, _log?.ElapsedMs ?? 0);
    }

    /// <summary>Audio/UI boundary; also accepts deterministic synthetic measurements for integration checks.</summary>
    public void ApplyPitchMeasurement(PitchMeasurement result, long timestampMs)
    {
        if (_exercisePaused || Melody.IsPaused) return;
        double? freeCents = null;
        if (FreeSingingEnabled)
        {
            var free = FreeSinging.Observe(result, Math.Max(0, timestampMs - _freeStartedMs));
            freeCents = free.DeviationCents; _log?.FreeSample(free, result); RefreshTunerReference();
        }
        else if (!Melody.IsPracticing) _log?.Sample(_targetMidi, result);
        RangeDiagnostics.OnMeasurement(result);
        Melody.OnMicrophoneSample(result.FrequencyHz, result.Confidence);
        if (_attemptAssessment is not null && _exerciseClock is not null)
            _attemptAssessment.Add(result, (long)(_exerciseClock.ElapsedSeconds * 1000));
        if (FeedbackHidden) return;
        double? cents = FreeSingingEnabled ? freeCents : result.FrequencyHz is { } hz && hz > 0 && double.IsFinite(hz) ? NoteCatalog.Cents(hz, NoteCatalog.ToFrequency(_targetMidi)) : null;
        if (result.FrequencyHz is { } frequency && frequency > 0 && double.IsFinite(frequency) && result.Confidence >= 0.65 && cents.HasValue)
        {
            int midi = NoteCatalog.NearestMidi(frequency);
            DetectedNoteText = NoteCatalog.ToLatin(midi);
            DetectedHzText = $"{frequency:F1} Гц  ·  {NoteCatalog.ToRussian(midi)}";
            DeviationText = $"{cents:+0;-0;0} ¢";
            double abs = Math.Abs(cents!.Value);
            AccuracyText = abs <= 25 ? "Точное попадание" : abs <= 50 ? "Близко к цели" : cents < 0 ? "Ниже цели" : "Выше цели";
            AccuracyBrush = MakeBrush(abs <= 25 ? "#6EE5B1" : abs <= 50 ? "#F0CE76" : "#FF9B81");
            TunerCents = cents.Value;
        }
        else
        {
            DetectedNoteText = "—";
            DetectedHzText = "Нет устойчивого тона";
            DeviationText = "— ¢";
            AccuracyText = "Ожидание голоса";
            AccuracyBrush = MakeBrush("#94A3BF");
            TunerCents = double.NaN;
        }
        ConfidenceText = $"Достоверность: {result.Confidence:P0}";
        long now = timestampMs;
        _history.Enqueue(new PitchPoint(now, cents));
        while (_history.TryPeek(out var oldest) && now - oldest.Milliseconds > 30000) _history.Dequeue();
        HistoryPoints = _history.ToArray();
    }

    private void ImportExercise()
    {
        string? path = _dialogs.SelectExercise();
        if (path is null) return;
        try
        {
            var loaded = ExerciseIO.Load(path);
            CancelExercise();
            SetExercise(loaded, path);
            SelectedTabIndex = 1;
        }
        catch (Exception ex) { ShowError("Импорт упражнения", ex); }
    }

    private void SetExercise(ExerciseDocument doc, string path)
    {
        bool bundled = string.Equals(Path.GetDirectoryName(Path.GetFullPath(path)), Path.Combine(AppContext.BaseDirectory, "exercises"), StringComparison.OrdinalIgnoreCase);
        SelectedExercise = _exerciseLibrary.Add(doc, path, !bundled);
        if (_exerciseLibrary.Warning is { } warning) StatusText = warning;
    }

    private void ActivateExercise(ExerciseDocument doc, string path)
    {
        _exercise = doc;
        ExerciseTitle = doc.Title;
        ExerciseDescription = doc.Description;
        ExerciseGoals = doc.Goals?.Count > 0 ? string.Join("  •  ", doc.Goals) : "Не заданы";
        ExerciseTasks = doc.Tasks?.Count > 0 ? string.Join("  •  ", doc.Tasks) : "Не заданы";
        int notes = doc.Sections.Sum(x => x.Notes.Count);
        double duration = doc.Sections.Sum(s => s.Notes.Sum(n => n.PreviewSeconds + n.HoldSeconds + n.TransitionSeconds));
        ExerciseMeta = $"{doc.Sections.Count} секц.   ·   {notes} нот   ·   {duration / 60:0.#} мин";
        ExerciseProgress = 0;
        ExercisePhase = "ГОТОВО";
        ExercisePosition = "Нажми «Начать упражнение»";
        ExerciseCountdown = "—";
        ShowSectionTimeline(0);
        SetTarget(ExerciseIO.ParseMidi(doc.Sections[0].Notes[0].Note), true);
        StatusText = "Импортировано: " + Path.GetFileName(path);
        if (!ExerciseActive) ExerciseEditor.LoadFrom(doc);
        RefreshCommands();
    }

    private void ShowSectionTimeline(int sectionIndex)
    {
        Timeline.Clear();
        if (_exercise is null) return;
        var section = _exercise.Sections[sectionIndex];
        SectionTitle = $"СЕКЦИЯ {sectionIndex + 1}/{_exercise.Sections.Count}  ·  {section.Title}";
        for (int i = 0; i < section.Notes.Count; i++)
        {
            var step = section.Notes[i];
            int midi = ExerciseIO.ParseMidi(step.Note);
            Timeline.Add(new TimelineStep(NoteCatalog.ToLatin(midi), $"{step.HoldSeconds:0.#} с", i + 1));
        }
    }

    private async Task RunExerciseAsync()
    {
        if (_exercise is null || _exerciseCancel is not null || !SessionActive || Melody.IsBusy || _calibrationStartedUtc is not null) return;
        if (FreeSingingEnabled) SetFreeSingingMode(false, true);
        var exercise = _exercise;
        using var cts = new CancellationTokenSource();
        _exerciseCancel = cts;
        _exerciseClock = new PausableClock();
        _exercisePaused = false;
        _repeatRequested = false;
        _successfulSteps = _evaluatedSteps = 0;
        _exerciseResults.Clear();
        LatestAssessmentText = SelectedFeedbackMode?.Code == "test"
            ? "Контрольный режим: все оценки появятся после упражнения."
            : "Упражнение началось. Результаты будут показаны после каждой ноты.";
        RefreshCommands();
        RefreshFeedbackVisibility();
        _log?.Event("exercise_start", new
        {
            exercise.SchemaVersion, exercise.Title, exercise.Description, exercise.Goals, exercise.Tasks,
            sectionCount = exercise.Sections.Count, feedbackMode = SelectedFeedbackMode?.Code,
            defaultToleranceCents = SelectedTolerance, defaultMaxAttempts = SelectedMaxAttempts,
            defaultAutoRepeat = AutoRepeatFailed
        });
        int completed = 0;
        int total = exercise.Sections.Sum(s => s.Notes.Count);
        try
        {
            for (int si = 0; si < exercise.Sections.Count; si++)
            {
                var section = exercise.Sections[si];
                ShowSectionTimeline(si);
                _log?.Event("exercise_section_start", new { sectionIndex = si, section.Title, section.Description });
                for (int ni = 0; ni < section.Notes.Count; ni++)
                {
                    cts.Token.ThrowIfCancellationRequested();
                    var step = section.Notes[ni];
                    int midi = ExerciseIO.ParseMidi(step.Note);
                    var settings = new AssessmentSettings(step.ToleranceCents ?? SelectedTolerance,
                        step.RequiredAccuracy ?? .75);
                    settings.Validate();
                    int maxAttempts = step.MaxAttempts ?? SelectedMaxAttempts;
                    bool repeat = step.AutoRepeatOnFail ?? AutoRepeatFailed;
                    SetTarget(midi, true);
                    foreach (var item in Timeline) { item.Active = item.Index == ni + 1; item.Completed = item.Index <= ni; }
                    bool skipped = false;
                    for (int attempt = 1; attempt <= (repeat ? maxAttempts : 1); attempt++)
                    {
                        _skipRequested = false;
                        _repeatRequested = false;
                        cts.Token.ThrowIfCancellationRequested();
                        ExercisePosition = $"Нота {completed + 1}/{total}: {NoteCatalog.ToBilingual(midi)} · попытка {attempt}/{(repeat ? maxAttempts : 1)}";
                        _log?.BeginStep(si, ni, midi, step);
                        _log?.Event("attempt_start", new { sectionIndex = si, noteIndex = ni, attempt,
                            targetMidi = midi, settings.ToleranceCents, settings.RequiredAccuracy });
                        _log?.SetPhase("preview", new { sectionIndex = si, noteIndex = ni, attempt });
                        _audio.PlayTone(NoteCatalog.ToFrequency(midi), step.PreviewSeconds);
                        await RunPhaseAsync("СЛУШАЙ ЭТАЛОН", step.PreviewSeconds, completed, total, cts.Token);
                        _audio.StopTone();
                        cts.Token.ThrowIfCancellationRequested();
                        if (_repeatRequested)
                        {
                            _log?.Event("exercise_attempt_repeated", new { sectionIndex = si, noteIndex = ni, phase = "preview" });
                            _log?.EndStep(si, ni, midi);
                            attempt--; continue;
                        }
                        if (_skipRequested) { _log?.EndStep(si, ni, midi); skipped = true; break; }
                        _log?.SetPhase("hold", new { sectionIndex = si, noteIndex = ni, attempt });
                        _holdStartedLogicalMs = (long)(_exerciseClock!.ElapsedSeconds * 1000);
                        _attemptAssessment = new AttemptAssessment(midi, settings, _holdStartedLogicalMs);
                        await RunPhaseAsync("ПОЙ И УДЕРЖИВАЙ", step.HoldSeconds, completed, total, cts.Token);
                        if (_repeatRequested)
                        {
                            _attemptAssessment = null;
                            _log?.Event("exercise_attempt_repeated", new { sectionIndex = si, noteIndex = ni, phase = "hold" });
                            _log?.EndStep(si, ni, midi);
                            attempt--;
                            continue;
                        }
                        _attemptAssessment ??= new AttemptAssessment(midi, settings, _holdStartedLogicalMs);
                        AssessmentResult result = _attemptAssessment.Finish();
                        _attemptAssessment = null;
                        _log?.Event("attempt_assessment", new { sectionIndex = si, noteIndex = ni, attempt,
                            targetNote = NoteCatalog.ToLatin(midi), result });
                        _log?.EndStep(si, ni, midi);
                        _exerciseResults.Add(result);
                        _evaluatedSteps++;
                        if (result.Success) _successfulSteps++;
                        if (SelectedFeedbackMode?.Code != "test")
                            LatestAssessmentText = $"{NoteCatalog.ToLatin(midi)} · попытка {attempt}: {result.Description}";
                        if (_skipRequested) { skipped = true; break; }
                        bool willRetry = !result.Success && repeat && attempt < maxAttempts;
                        _log?.SetPhase("transition", new { sectionIndex = si, noteIndex = ni, attempt });
                        await RunPhaseAsync(willRetry ? "ОТДЫХ / ПОВТОР" : "ПАУЗА", step.TransitionSeconds, completed, total, cts.Token);
                        cts.Token.ThrowIfCancellationRequested();
                        if (_skipRequested) { skipped = true; break; }
                        if (_repeatRequested) { attempt--; continue; }
                        if (result.Success || !repeat || attempt == maxAttempts) break;
                    }
                    if (skipped) _log?.Event("exercise_step_skipped", new { sectionIndex = si, noteIndex = ni, targetMidi = midi });
                    Timeline[ni].Active = false;
                    Timeline[ni].Completed = true;
                    completed++;
                    ExerciseProgress = 100.0 * completed / total;
                }
            }
            _log?.Event("exercise_end", new { result = "completed", attempts = _evaluatedSteps,
                successfulAttempts = _successfulSteps, steps = total });
            ExercisePhase = "ГОТОВО";
            ExerciseCountdown = "0,0 с";
            ExercisePosition = "Упражнение завершено. Статистика сохранена в JSONL.";
            if (SelectedFeedbackMode?.Code == "test")
                LatestAssessmentText = $"Контроль: {_successfulSteps}/{_evaluatedSteps} удачных попыток. " +
                    $"Средняя точность {(_exerciseResults.Count == 0 ? 0 : _exerciseResults.Average(r => r.AccuratePercent)):0.#}% среди распознанных кадров.";
        }
        catch (OperationCanceledException)
        {
            _log?.Event("exercise_end", new { result = "cancelled", attempts = _evaluatedSteps });
            ExercisePhase = "ОСТАНОВЛЕНО";
            ExerciseCountdown = "—";
            ExercisePosition = "Упражнение прервано.";
        }
        catch (Exception ex)
        {
            _log?.Event("exercise_end", new { result = "error", error = ex.Message });
            ShowError("Выполнение упражнения", ex);
        }
        finally
        {
            _attemptAssessment = null;
            _skipRequested = false;
            _audio.StopTone();
            _log?.SetPhase("manual");
            _exerciseCancel = null;
            _exerciseClock?.Stop();
            _exerciseClock = null;
            _exercisePaused = false;
            _phasePreview = false;
            _repeatRequested = false;
            RefreshFeedbackVisibility();
            RefreshCommands();
        }
    }

    private async Task RunPhaseAsync(string name, double seconds, int finished, int total, CancellationToken token)
    {
        ExercisePhase = name;
        _unpausedPhase = name;
        _phasePreview = name == "СЛУШАЙ ЭТАЛОН";
        _phaseStartSeconds = _exerciseClock!.ElapsedSeconds;
        _phaseSeconds = seconds;
        RefreshFeedbackVisibility();
        try
        {
            while (true)
            {
                token.ThrowIfCancellationRequested();
                if (_skipRequested || _repeatRequested) break;
                double remaining = Math.Max(0, seconds - (_exerciseClock.ElapsedSeconds - _phaseStartSeconds));
                ExerciseCountdown = $"{remaining:F1} с";
                if (!_exercisePaused && remaining <= 0) break;
                await Task.Delay(TimeSpan.FromMilliseconds(45), token);
            }
        }
        finally { _phasePreview = false; }
        ExerciseProgress = 100.0 * finished / total;
    }

    private void RequestSkip()
    {
        if (_exercisePaused) ToggleExercisePause();
        _skipRequested = true;
        _audio.StopTone();
    }

    private void RequestRepeat()
    {
        if (_exercisePaused) ToggleExercisePause();
        _repeatRequested = true;
        _audio.StopTone();
    }

    private void ToggleExercisePause()
    {
        if (_exerciseClock is null || !ExerciseActive) return;
        if (_exercisePaused)
        {
            _exerciseClock.Resume();
            _exercisePaused = false;
            ExercisePhase = _unpausedPhase;
            if (_phasePreview)
            {
                double remaining = Math.Max(0, _phaseSeconds - (_exerciseClock.ElapsedSeconds - _phaseStartSeconds));
                if (remaining > .05) _audio.PlayTone(NoteCatalog.ToFrequency(_targetMidi), remaining);
            }
            _log?.Event("exercise_resumed", new { phase = ExercisePhase });
        }
        else
        {
            _exerciseClock.Pause();
            _exercisePaused = true;
            _audio.StopTone();
            ExercisePhase = "ПАУЗА";
            _log?.Event("exercise_paused", new { phase = _unpausedPhase });
        }
        RefreshCommands();
        RefreshFeedbackVisibility();
    }

    public void ActivateShortcut(string id)
    {
        // Called only after scope matching and text-input checks by the Window adapter.
        switch (id)
        {
            case "tab1": SelectedTabIndex = 0; break;
            case "tab2": SelectedTabIndex = 1; break;
            case "tab3": SelectedTabIndex = 2; break;
            case "tab4": SelectedTabIndex = 3; break;
            case "tab5": SelectedTabIndex = 4; break;
            case "tab6": SelectedTabIndex = 5; break;
            case "help": SelectedTabIndex = 6; break;
            case "mic": if (ToggleSessionCommand.CanExecute(null)) ToggleSessionCommand.Execute(null); break;
            case "record": if (ToggleRecordingCommand.CanExecute(null)) ToggleRecordingCommand.Execute(null); break;
            case "stop":
                if (ExerciseActive) CancelExercise();
                else if (Melody.IsBusy) Melody.Stop();
                else if (RangeDiagnostics.IsMeasuring) RangeDiagnostics.Cancel();
                else { EarTraining.Stop(); _audio.StopTone(); }
                break;
            case "trainer_toggle":
                if (ExerciseActive) ToggleExercisePause();
                else if (CanStartExercise) StartExerciseCommand.Execute(null);
                break;
            case "trainer_repeat":
                if (RepeatStepCommand.CanExecute(null)) RepeatStepCommand.Execute(null);
                else if (PlayToneCommand.CanExecute(null)) PlayToneCommand.Execute(null);
                break;
            case "trainer_skip": if (SkipStepCommand.CanExecute(null)) SkipStepCommand.Execute(null); break;
            case "trainer_repeat_tone":
            case "trainer_preview": if (PlayToneCommand.CanExecute(null)) PlayToneCommand.Execute(null); break;
            case "ear_next": if (EarTraining.NewChallengeCommand.CanExecute(null)) EarTraining.NewChallengeCommand.Execute(null); break;
            case "ear_higher": if (EarTraining.HigherCommand.CanExecute(null)) EarTraining.HigherCommand.Execute(null); break;
            case "ear_lower": if (EarTraining.LowerCommand.CanExecute(null)) EarTraining.LowerCommand.Execute(null); break;
            case "ear_same": if (EarTraining.SameCommand.CanExecute(null)) EarTraining.SameCommand.Execute(null); break;
            case "ear_replay": if (EarTraining.ReplayCommand.CanExecute(null)) EarTraining.ReplayCommand.Execute(null); break;
            case "range_check": if (RangeDiagnostics.StartCheckCommand.CanExecute(null)) RangeDiagnostics.StartCheckCommand.Execute(null); break;
            case "midi_toggle": Melody.TogglePlaybackPause(); break;
            case "midi_repeat": Melody.Repeat(); break;
            case "midi_preview": if (Melody.PreviewCommand.CanExecute(null)) Melody.PreviewCommand.Execute(null); break;
            case "import":
                if (SelectedTabIndex == 5) { if (Melody.ImportCommand.CanExecute(null)) Melody.ImportCommand.Execute(null); }
                else if (SelectedTabIndex == 4) { if (ExerciseEditor.ImportCommand.CanExecute(null)) ExerciseEditor.ImportCommand.Execute(null); }
                else if (ImportExerciseCommand.CanExecute(null)) ImportExerciseCommand.Execute(null);
                break;
            case "save": if (SelectedTabIndex == 4 && ExerciseEditor.SaveCommand.CanExecute(null)) ExerciseEditor.SaveCommand.Execute(null); break;
        }
    }

    private void CancelExercise()
    {
        _exerciseCancel?.Cancel();
        _audio.StopTone();
    }

    private void SaveNote()
    {
        if (_log is null) { StatusText = "Чтобы добавить заметку, включи занятие."; return; }
        string message = TrainingNote.Trim();
        if (message.Length == 0) return;
        _log.Event("user_note", new { text = message });
        TrainingNote = "";
        StatusText = "Заметка добавлена в JSONL.";
    }

    private void ShowError(string title, Exception exception)
    {
        StatusText = $"{title}: {exception.Message}";
        if (!_disposed) _dialogs.Error(title, exception.Message);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        CancelExercise();
        RangeDiagnostics.Cancel();
        Melody.PropertyChanged -= OnStudyStateChanged;
        Melody.Dispose();
        EarTraining.Dispose();
        EarTraining.PropertyChanged -= OnStudyStateChanged;
        RangeDiagnostics.PropertyChanged -= OnStudyStateChanged;
        _uiTimer.Stop();
        _uiTimer.Tick -= OnTick;
        _audio.MonitoringStopped -= OnMonitoringStopped;
        _audio.Dispose();
        if (FreeSingingEnabled) _log?.Event("free_singing_end", FreeSinging.Statistics);
        _log?.Finish();
        _log = null;
    }
}
