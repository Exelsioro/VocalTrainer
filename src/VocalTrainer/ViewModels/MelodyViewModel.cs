using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using VocalTrainer.Infrastructure;
using VocalTrainer.Melody;

namespace VocalTrainer.ViewModels;

public sealed record ReferencePitch(string Note, string Octave, double FrequencyHz, int Count, double Seconds);

/// <summary>MIDI melody trainer with an optional persistent song and lyrics library.</summary>
public sealed class MelodyViewModel : ObservableObject, IDisposable
{
    private readonly Action<double, double> _play;
    private readonly Action _stopTone;
    private readonly Func<bool> _canUse;
    private readonly Func<bool> _microphoneActive;
    private readonly Func<string?> _selectFile;
    private readonly Action<string, object> _log;
    private readonly Action<string, Exception> _error;
    private MidiSong? _song;
    private MidiTrack? _track;
    private readonly SongLibrary? _library;
    private SavedSong? _selectedSong;
    private bool _loadingSong;
    private string _libraryStatus = "Импортируй MIDI, чтобы добавить песню в библиотеку.";
    public ObservableCollection<SavedSong> Songs { get; } = [];
    public string LibraryStatus { get => _libraryStatus; private set => Set(ref _libraryStatus, value); }
    public SavedSong? SelectedSong
    {
        get => _selectedSong;
        set { if (value is not null && value != _selectedSong && CanImport && !_loadingSong) OpenSavedSong(value); }
    }
    private CancellationTokenSource? _previewCancel;
    private CancellationTokenSource? _countInCancel;
    private CancellationTokenSource? _practiceTimelineCancel;
    private int? _practiceSoundingIndex;
    private bool _singWithReference;
    private bool _scoringAfterSeek;
    private PausableClock? _clock;
    private PausableClock? _previewClock;
    private PausableClock? _countClock;
    private bool _paused;
    private bool _disposed;
    private int? _previewSoundingIndex;
    private MelodyScorer? _scorer;
    private bool _practicing;
    private bool _countingIn;
    private bool _playingPreview;
    private int _speedPercent = 100;
    private int _transpose;
    private string _transposeText = "0";
    private double _viewStart;
    private int _viewWindowSeconds = 20;
    private bool _followPlayhead = true;
    private int _loops = 1;
    private int _lastLoop;
    private string _fragmentFrom = "0";
    private string _fragmentTo = "";
    private string _songInfo = "Импортируй .mid/.midi с вокальной мелодией.";
    private string _trackWarning = "Выбирай отдельную вокальную MIDI-дорожку, если она есть.";
    private string _status = "MIDI ещё не загружен";
    private string _currentTarget = "—";
    private string _currentError = "—";
    private string _currentVoice = "Голос появится после начала практики";
    private string _summary = "После практики появятся точность, покрытие голосом и результат каждой ноты.";
    private string _countdown = "";
    private double _cursor;
    private double _duration = 1;
    private IReadOnlyList<TimedTarget> _targets = [];
    private readonly List<VoiceSample> _voices = [];
    private IReadOnlyList<VoiceSample> _voice = [];
    private double _preparedFrom, _preparedSpeed = 1;
    public LyricsViewModel Lyrics { get; }
    public double SourceSeconds => _preparedFrom + Cursor * _preparedSpeed;
    public bool SingWithReference { get => _singWithReference; set { if (!IsBusy) Set(ref _singWithReference, value); } }
    private IReadOnlyList<ReferencePitch> _referencePitches = [];
    public IReadOnlyList<ReferencePitch> ReferencePitches { get => _referencePitches; private set => Set(ref _referencePitches, value); }
    private string _referenceSummary = "Импортируй MIDI для анализа нот и октав эталона.";
    public string ReferenceSummary { get => _referenceSummary; private set => Set(ref _referenceSummary, value); }

    public ObservableCollection<MidiTrack> Tracks { get; } = [];
    public IReadOnlyList<int> SpeedOptions { get; } = [50, 60, 70, 80, 90, 100, 110, 120, 130, 140, 150];
    public IReadOnlyList<int> ViewWindowOptions { get; } = [10, 20, 30, 60];
    public IReadOnlyList<int> LoopOptions { get; } = Enumerable.Range(1, 10).ToArray();
    public MidiTrack? SelectedTrack
    {
        get => _track;
        set
        {
            if (IsBusy || !Set(ref _track, value)) return;
            if (value is not null && value.Notes.Any(n => n.Midi + Transpose is < 0 or > 127))
            {
                Set(ref _transpose, 0, nameof(Transpose));
                TransposeText = "0";
            }
            OnChanged(nameof(TransposeDescription));
            if (value is null)
            {
                Targets = []; _voices.Clear(); Voice = [];
                ReferencePitches = []; ReferenceSummary = "Импортируй MIDI для анализа нот и октав эталона.";
                Duration = 1; Cursor = 0;
                Refresh();
            }
            else { Rebuild(true); Lyrics.OnTrackChanged(value.Index); PersistSong(); }
        }
    }
    public int SpeedPercent { get => _speedPercent; set { if (Set(ref _speedPercent, value)) Rebuild(false); } }
    public int Transpose { get => _transpose; set => ApplyTranspose(value); }
    public string TransposeText { get => _transposeText; set => Set(ref _transposeText, value); }
    public string TransposeDescription => _track is null || _track.Notes.Count == 0 ? "Положительное число — выше, отрицательное — ниже. 12 полутонов = октава." :
        $"Вся партия: {Transpose:+0;-0;0} пт · {NoteCatalog.ToLatin(_track.Notes.Min(n => n.Midi) + Transpose)}–{NoteCatalog.ToLatin(_track.Notes.Max(n => n.Midi) + Transpose)} · допустимо { -_track.Notes.Min(n => n.Midi)}…{127 - _track.Notes.Max(n => n.Midi)} пт";
    public int ViewWindowSeconds
    {
        get => _viewWindowSeconds;
        set { if (value > 0 && Set(ref _viewWindowSeconds, value)) { OnChanged(nameof(MaxViewStart)); UpdateViewport(); } }
    }
    public double MaxViewStart => Math.Max(0, Duration - ViewWindowSeconds);
    public double ViewStart
    {
        get => _viewStart;
        set { if (double.IsFinite(value) && Set(ref _viewStart, Math.Clamp(value, 0, MaxViewStart))) FollowPlayhead = false; }
    }
    public bool FollowPlayhead
    {
        get => _followPlayhead;
        set { if (Set(ref _followPlayhead, value) && value) UpdateViewport(); }
    }
    public string DisplayedLoop => $"Повтор {_lastLoop + 1}/{LoopCount} · на графике исполнение этого повтора";
    public int LoopCount
    {
        get => _loops;
        set
        {
            if (IsBusy || value is < 1 or > 10 || !Set(ref _loops, value)) return;
            _lastLoop = 0;
            _voices.Clear(); Voice = [];
            OnChanged(nameof(ProgressPercent)); OnChanged(nameof(DisplayedLoop));
        }
    }
    public double ProgressPercent => Duration <= 0 ? 0 : 100 * Math.Clamp((Cursor + _lastLoop * Duration) / (Duration * LoopCount), 0, 1);
    public string FragmentFrom { get => _fragmentFrom; set => Set(ref _fragmentFrom, value); }
    public string FragmentTo { get => _fragmentTo; set => Set(ref _fragmentTo, value); }
    public string SongInfo { get => _songInfo; private set => Set(ref _songInfo, value); }
    public string TrackWarning { get => _trackWarning; private set => Set(ref _trackWarning, value); }
    public string Status { get => _status; private set => Set(ref _status, value); }
    public string CurrentTarget { get => _currentTarget; private set => Set(ref _currentTarget, value); }
    public string CurrentError { get => _currentError; private set => Set(ref _currentError, value); }
    public string CurrentVoice { get => _currentVoice; private set => Set(ref _currentVoice, value); }
    public string Summary { get => _summary; private set => Set(ref _summary, value); }
    public string Countdown { get => _countdown; private set => Set(ref _countdown, value); }
    public double Cursor { get => _cursor; private set { if (Set(ref _cursor, value)) { OnChanged(nameof(ProgressPercent)); UpdateViewport(); Lyrics.Highlight(SourceSeconds); } } }
    public double Duration { get => _duration; private set { if (Set(ref _duration, value)) { OnChanged(nameof(MaxViewStart)); UpdateViewport(); } } }
    public IReadOnlyList<TimedTarget> Targets { get => _targets; private set => Set(ref _targets, value); }
    public IReadOnlyList<VoiceSample> Voice { get => _voice; private set => Set(ref _voice, value); }
    public bool IsPracticing { get => _practicing; private set { if (Set(ref _practicing, value)) OnChanged(nameof(IsBusy)); } }
    public bool IsCountingIn { get => _countingIn; private set { if (Set(ref _countingIn, value)) OnChanged(nameof(IsBusy)); } }
    public bool IsPreviewing { get => _playingPreview; private set { if (Set(ref _playingPreview, value)) OnChanged(nameof(IsBusy)); } }
    public bool IsBusy => IsPracticing || IsPreviewing || IsCountingIn;
    public bool IsPaused { get => _paused; private set { if (Set(ref _paused, value)) OnChanged(nameof(PauseButtonText)); } }
    public string PauseButtonText => IsPaused ? "▶ Продолжить [Space]" : "Ⅱ Пауза [Space]";
    public bool CanImport => !_disposed && !IsBusy && _canUse();
    public bool CanPrepare => SelectedTrack is not null && CanImport;
    public bool CanPractice => CanPrepare && _microphoneActive();
    public bool CanStop => IsBusy;

    public RelayCommand ImportCommand { get; }
    public RelayCommand PrepareCommand { get; }
    public RelayCommand PreviewCommand { get; }
    public RelayCommand PracticeCommand { get; }
    public RelayCommand StopCommand { get; }
    public RelayCommand ApplyTransposeCommand { get; }
    public RelayCommand TransposeUpCommand { get; }
    public RelayCommand TransposeDownCommand { get; }
    public RelayCommand ResetTransposeCommand { get; }
    public RelayCommand<double> SeekCommand { get; }
    public RelayCommand<double> PanCommand { get; }

    public MelodyViewModel(Action<double, double> play, Action stopTone, Func<bool> canUse,
        Func<bool> microphoneActive, Func<string?> selectFile, Action<string, object> log,
        Action<string, Exception> error, Func<string?>? selectLyrics = null, Func<string?>? saveLyrics = null,
        SongLibrary? songLibrary = null)
    {
        _play = play; _stopTone = stopTone; _canUse = canUse;
        _microphoneActive = microphoneActive; _selectFile = selectFile; _log = log; _error = error;
        _library = songLibrary;
        ImportCommand = new RelayCommand(Import, () => CanImport);
        PrepareCommand = new RelayCommand(Prepare, () => CanPrepare);
        PreviewCommand = new RelayCommand(() => _ = PreviewAsync(), () => CanPrepare);
        PracticeCommand = new RelayCommand(() => _ = PracticeAsync(), () => CanPractice);
        StopCommand = new RelayCommand(Stop, () => CanStop);
        ApplyTransposeCommand = new RelayCommand(ApplyTransposeText, () => CanPrepare);
        TransposeUpCommand = new RelayCommand(() => ApplyTranspose(Transpose + 1), () => CanPrepare);
        TransposeDownCommand = new RelayCommand(() => ApplyTranspose(Transpose - 1), () => CanPrepare);
        ResetTransposeCommand = new RelayCommand(() => ApplyTranspose(0), () => CanPrepare);
        SeekCommand = new(Seek, value => !_disposed && Targets.Count > 0 && double.IsFinite(value));
        PanCommand = new(value => { FollowPlayhead = false; ViewStart = value; }, value => !_disposed && Targets.Count > 0 && double.IsFinite(value));
        Lyrics = new LyricsViewModel(() => CanPrepare, () => !_disposed && (CanPrepare || IsPreviewing),
            () => SourceSeconds, () => _track?.EndSeconds ?? 0,
            (from, to) => { FragmentFrom = from.ToString("0.###", CultureInfo.InvariantCulture); FragmentTo = to.ToString("0.###", CultureInfo.InvariantCulture); TryBuild(true); },
            selectLyrics ?? (() => null), saveLyrics ?? (() => null));
        Lyrics.ContentChanged += PersistSong;
        if (_library is not null)
        {
            _library.Load();
            foreach (var song in _library.Entries) Songs.Add(song);
            var selected = Songs.FirstOrDefault(s => s.Id == _library.LastSelectedId) ?? Songs.FirstOrDefault();
            if (selected is not null) OpenSavedSong(selected);
            if (_library.Warning is { } warning) LibraryStatus = warning;
        }
    }

    private void PersistSong(string? importedText = null)
    {
        if (_loadingSong || _library is null || _selectedSong is null) return;
        try
        {
            _library.Save(_selectedSong, SelectedTrack?.Index, Lyrics.Snapshot(), importedText);
            LibraryStatus = "MIDI, текст и разметка сохранены в библиотеке автоматически.";
        }
        catch (Exception ex) { LibraryStatus = "Изменения доступны сейчас, но не сохранены: " + ex.Message; }
    }

    private void LoadSong(MidiSong song, int? preferredTrack = null, SavedLyrics? lyrics = null)
    {
        _song = song;
        Tracks.Clear();
        foreach (var track in song.Tracks) Tracks.Add(track);
        Set(ref _transpose, 0, nameof(Transpose)); TransposeText = "0";
        SelectedTrack = Tracks.FirstOrDefault(t => t.Index == preferredTrack) ??
            Tracks.OrderBy(t => t.OverlapCount).ThenByDescending(t => t.Notes.Count).First();
        Lyrics.ResetMidiSources(song.LyricTracks, SelectedTrack.Index);
        if (lyrics is not null) Lyrics.Restore(lyrics);
        SongInfo = $"{song.FileName} · {Tracks.Count} дорожек с нотами · PPQ {song.TicksPerQuarter}";
        Status = "MIDI загружен. Выбери дорожку, настрой параметры и нажми «Применить фрагмент».";
    }

    private void OpenSavedSong(SavedSong entry)
    {
        if (_library is null) return;
        try
        {
            var state = _library.Read(entry);
            var song = MidiSongReader.Read(_library.MidiPath(entry)) with { FileName = state.Title };
            _loadingSong = true;
            LoadSong(song, state.TrackIndex, state.Lyrics);
            Set(ref _selectedSong, entry, nameof(SelectedSong));
            _library.Select(entry);
            LibraryStatus = "Песня и связанный текст восстановлены из локальной библиотеки.";
        }
        catch (Exception ex) { LibraryStatus = "Не удалось открыть сохранённую песню: " + ex.Message; OnChanged(nameof(SelectedSong)); }
        finally { _loadingSong = false; Refresh(); }
    }

    public void Refresh()
    {
        OnChanged(nameof(CanImport)); OnChanged(nameof(CanPrepare)); OnChanged(nameof(CanPractice));
        OnChanged(nameof(CanStop));
        ImportCommand.Refresh(); PrepareCommand.Refresh(); PreviewCommand.Refresh();
        PracticeCommand.Refresh(); StopCommand.Refresh();
        ApplyTransposeCommand.Refresh(); TransposeUpCommand.Refresh(); TransposeDownCommand.Refresh(); ResetTransposeCommand.Refresh();
        Lyrics.Refresh();
        SeekCommand.Refresh(); PanCommand.Refresh();
    }

    public void Seek(double seconds)
    {
        if (_disposed || Targets.Count == 0 || !double.IsFinite(seconds)) return;
        double previous = Cursor;
        seconds = Math.Clamp(seconds, 0, IsBusy ? Math.Max(0, Duration - .001) : Duration);
        FollowPlayhead = false;
        if (IsPreviewing || IsPracticing) _stopTone();
        _previewClock?.Seek(_lastLoop * Duration + seconds);
        _clock?.Seek(_lastLoop * Duration + seconds);
        _previewSoundingIndex = _practiceSoundingIndex = null;
        if (IsPracticing)
        {
            _scorer = new MelodyScorer(Targets); _scoringAfterSeek = true;
            _voices.Clear(); Voice = [];
            Summary = "Переход по времени: оценка и голосовой след начаты заново с выбранного места.";
        }
        Cursor = seconds;
        var target = MelodyPlanner.At(Targets, seconds);
        CurrentTarget = target is null ? "ПАУЗА В МЕЛОДИИ" : NoteCatalog.ToBilingual(target.Midi);
        CurrentError = "—";
        if (seconds < ViewStart || seconds > ViewStart + ViewWindowSeconds)
            ViewStart = Math.Clamp(seconds - ViewWindowSeconds / 2.0, 0, MaxViewStart);
        Lyrics.Highlight(SourceSeconds);
        Status = IsPracticing ? "Позиция изменена. Оценивается пение после последнего перехода." : $"Позиция: {seconds:0.00} с фрагмента.";
        _log("midi_seek", new { previousSeconds = previous, seconds, sourceSeconds = SourceSeconds,
            lap = _lastLoop + 1, scoringReset = IsPracticing, paused = IsPaused });
    }

    private void UpdateViewport()
    {
        double start = Math.Clamp(_viewStart, 0, MaxViewStart);
        if (FollowPlayhead && (Cursor < start || Cursor > start + ViewWindowSeconds * .85))
            start = Math.Clamp(Cursor - ViewWindowSeconds * .75, 0, MaxViewStart);
        Set(ref _viewStart, start, nameof(ViewStart));
    }

    private void ApplyTransposeText()
    {
        if (!int.TryParse(TransposeText.Trim().Replace('−', '-'), NumberStyles.Integer, CultureInfo.InvariantCulture, out int value))
        { Status = "Укажи целое число полутонов: например +3, -5 или 12."; return; }
        ApplyTranspose(value);
    }

    private void ApplyTranspose(int value)
    {
        if (!CanPrepare) return;
        if (TryBuild(false, value))
        {
            Status = $"Вся партия сдвинута на {Transpose:+0;-0;0} полутонов. График, эталон и оценка используют новую высоту.";
            _log("midi_transposed", new { track = _track?.Index, semitones = Transpose });
        }
    }

    private void Import()
    {
        var path = _selectFile();
        if (string.IsNullOrWhiteSpace(path)) return;
        try
        {
            if (_library is not null)
            {
                var entry = _library.Import(path);
                if (!Songs.Any(s => s.Id == entry.Id)) Songs.Add(entry);
                OpenSavedSong(entry);
            }
            else
            {
                _loadingSong = true;
                try { LoadSong(MidiSongReader.Read(path)); }
                finally { _loadingSong = false; }
            }
            var song = _song;
            if (song is null) return;
            _log("midi_import", new { fileName = song.FileName, trackCount = Tracks.Count,
                tracks = Tracks.Select(t => new { t.Index, t.Title, notes = t.Notes.Count, t.OverlapCount }).ToArray() });
        }
        catch (Exception ex) { _error("Импорт MIDI", ex); }
        Refresh();
    }

    private static double ParseSeconds(string value)
    {
        if (!double.TryParse(value.Trim().Replace(',', '.'), NumberStyles.Float,
                CultureInfo.InvariantCulture, out var parsed) || !double.IsFinite(parsed))
            throw new FormatException("Укажи время в секундах, например 12.5.");
        return parsed;
    }
    private bool TryBuild(bool report, int? transposeOverride = null, bool preserveCursor = false)
    {
        if (_track is null) return false;
        try
        {
            double from = ParseSeconds(FragmentFrom);
            double to = string.IsNullOrWhiteSpace(FragmentTo) ? _track.EndSeconds : ParseSeconds(FragmentTo);
            int transpose = transposeOverride ?? Transpose;
            var result = MelodyPlanner.Build(_track, SpeedPercent / 100.0, transpose, from, to);
            if (result.Count == 0) throw new FormatException("В выбранном фрагменте нет нот.");
            double nextDuration = (to - from) / (SpeedPercent / 100.0);
            double startCursor = preserveCursor && Targets.SequenceEqual(result) && Math.Abs(Duration - nextDuration) < .000001 && Cursor < nextDuration ? Cursor : 0;
            Set(ref _transpose, transpose, nameof(Transpose));
            TransposeText = Transpose.ToString(CultureInfo.InvariantCulture);
            OnChanged(nameof(TransposeDescription));
            Targets = result;
            ReferencePitches = result.GroupBy(n => n.Midi).OrderBy(g => g.Key)
                .Select(g => new ReferencePitch(NoteCatalog.ToLatin(g.Key), $"{g.Key / 12 - 1} — {NoteCatalog.ToOctaveName(g.Key / 12 - 1)}",
                    NoteCatalog.ToFrequency(g.Key), g.Count(), g.Sum(n => n.DurationSeconds))).ToArray();
            ReferenceSummary = $"Эталон: {result.Count} нот · диапазон {NoteCatalog.ToBilingual(result.Min(n => n.Midi))} — {NoteCatalog.ToBilingual(result.Max(n => n.Midi))}. " +
                "Октавы: " + string.Join(", ", result.Select(n => n.Midi / 12 - 1).Distinct().Order().Select(NoteCatalog.ToOctaveName));
            _preparedFrom = from; _preparedSpeed = SpeedPercent / 100.0;
            Duration = nextDuration;
            Cursor = startCursor; _lastLoop = 0;
            Set(ref _viewStart, Math.Clamp(startCursor - ViewWindowSeconds / 2.0, 0, MaxViewStart), nameof(ViewStart));
            OnChanged(nameof(DisplayedLoop));
            _voices.Clear(); Voice = [];
            CurrentError = "—";
            CurrentVoice = "Голос появится после начала практики";
            CurrentTarget = "—";
            Lyrics.Highlight(SourceSeconds);
            TrackWarning = _track.Notes.Any(n => n.Channel == 9)
                ? "В дорожке встречается ударный канал 10; для пения лучше выбрать другую дорожку."
                : _track.OverlapCount > 0
                ? "Внимание: полифоническая дорожка. Для вокальной линии выбирается верхняя звучащая нота."
                : "Монофоническая дорожка · подходит для повторения мелодии.";
            if (report)
            {
                Status = $"Фрагмент {from:0.0}–{to:0.0} с · {Targets.Count} целевых нот · скорость {SpeedPercent}% · сдвиг {Transpose:+0;-0;0} полутонов";
                _log("midi_fragment_prepared", new { fileName = _song?.FileName, track = _track.Index,
                    fromSeconds = from, toSeconds = to, speedPercent = SpeedPercent,
                    transposeSemitones = Transpose, targetCount = Targets.Count });
            }
            Refresh();
            return true;
        }
        catch (Exception ex)
        {
            Status = "Параметры не применены: " + ex.Message;
            if (report) _error("Параметры MIDI", ex);
            return false;
        }
    }
    private void Rebuild(bool resetBounds)
    {
        if (IsBusy) return;
        if (_track is null) return;
        if (resetBounds)
        {
            FragmentFrom = "0";
            FragmentTo = _track.EndSeconds.ToString("0.########", CultureInfo.InvariantCulture);
        }
        TryBuild(false);
        Refresh();
    }
    private void Prepare() => TryBuild(true);

    private async Task PreviewAsync()
    {
        if (!TryBuild(true, preserveCursor: true)) return;
        using var cancel = new CancellationTokenSource();
        _previewCancel = cancel;
        _previewClock = new PausableClock();
        _previewClock.Seek(Cursor);
        FollowPlayhead = true;
        _previewSoundingIndex = null;
        IsPaused = false;
        IsPreviewing = true; Refresh();
        _log("midi_preview_start", new { track = SelectedTrack?.Index, Duration, SpeedPercent, Transpose, LoopCount });
        try
        {
            while (true)
            {
                cancel.Token.ThrowIfCancellationRequested();
                if (IsPaused) { await Task.Delay(35, cancel.Token); continue; }
                double elapsed = _previewClock.ElapsedSeconds;
                if (elapsed >= Duration * LoopCount) { Cursor = Duration; break; }
                int lap = (int)(elapsed / Duration);
                if (lap != _lastLoop)
                {
                    _lastLoop = lap; _previewSoundingIndex = null;
                    _stopTone(); OnChanged(nameof(DisplayedLoop));
                }
                double seconds = elapsed - lap * Duration;
                Cursor = seconds;
                var target = MelodyPlanner.At(Targets, seconds);
                int? index = target?.Index;
                if (index != _previewSoundingIndex)
                {
                    _stopTone(); _previewSoundingIndex = index;
                    if (target is not null)
                    {
                        CurrentTarget = NoteCatalog.ToBilingual(target.Midi);
                        _play(NoteCatalog.ToFrequency(target.Midi), Math.Max(.01, target.EndSeconds - seconds));
                    }
                }
                await Task.Delay(35, cancel.Token);
            }
            Status = "Прослушивание эталона завершено.";
        }
        catch (OperationCanceledException) { Status = "Прослушивание остановлено."; }
        catch (Exception ex) { _error("Прослушивание MIDI", ex); }
        finally
        {
            _stopTone();
            _previewSoundingIndex = null;
            _previewClock?.Stop(); _previewClock = null;
            _previewCancel = null;
            IsPaused = false;
            IsPreviewing = false;
            _log("midi_preview_end", new { cursor = Cursor, cancelled = cancel.IsCancellationRequested });
            Refresh();
        }
    }

    private async Task PracticeAsync()
    {
        if (!TryBuild(true, preserveCursor: true) || !_microphoneActive()) return;
        FollowPlayhead = true;
        var cancel = new CancellationTokenSource();
        _countInCancel = cancel;
        _countClock = new PausableClock();
        IsPaused = false;
        IsCountingIn = true;
        _log("midi_practice_start", new { fileName = _song?.FileName, trackIndex = SelectedTrack?.Index,
            speedPercent = SpeedPercent, transposeSemitones = Transpose, duration = Duration,
            fragmentStartSeconds = _preparedFrom, sourceSecondsPerPracticeSecond = _preparedSpeed, startCursorSeconds = Cursor,
            notes = Targets.Count, targets = Targets, loops = LoopCount, scoringToleranceCents = 40, singWithReference = SingWithReference });
        Refresh();
        try
        {
            for (int n = 3; n >= 1; n--)
            {
                Countdown = $"Начало через {n}…";
                Status = "Вдохни спокойно, приготовься спеть мелодию без звуковой подложки.";
                await _countClock.DelayAsync(1, cancel.Token);
            }
            Countdown = "";
            _scorer = new MelodyScorer(Targets);
            Summary = "Практика идёт: результаты появятся после окончания.";
            _lastLoop = 0;
            _countClock?.Stop(); _countClock = null;
            _clock = new PausableClock();
            _clock.Seek(Cursor);
            _scoringAfterSeek = Cursor > 0;
            IsPracticing = true;
            IsCountingIn = false;
            _practiceSoundingIndex = null;
            var timelineCancellation = new CancellationTokenSource();
            _practiceTimelineCancel = timelineCancellation;
            _ = RunPracticeTimelineAsync(_clock, timelineCancellation);
            Status = SingWithReference ? "Пой вместе с эталоном в наушниках. Голос показан поверх нот и оценивается." :
                "Пой по синим нотам. Твой голос поверх эталона: зелёный — в пределах ±40 ¢, оранжевый — отклонение.";
        }
        catch (OperationCanceledException)
        {
            Countdown = ""; Status = "Подготовка остановлена.";
            _log("midi_practice_end", new { status = "cancelled_count_in" });
        }
        finally
        {
            _countInCancel = null;
            _countClock?.Stop(); _countClock = null;
            cancel.Dispose();
            IsCountingIn = false;
            Refresh();
        }
    }

    public void OnMicrophoneSample(double? frequencyHz, double confidence, double? rms = null)
    {
        if (!IsPracticing || _clock is null || IsPaused) return;
        double elapsed = _clock.ElapsedSeconds;
        if (!UpdatePracticePosition(elapsed)) return;
        double seconds = elapsed - _lastLoop * Duration;
        var target = MelodyPlanner.At(Targets, seconds);
        bool reliable = frequencyHz is > 0 && double.IsFinite(frequencyHz.Value) &&
            double.IsFinite(confidence) && confidence >= .65;
        if (reliable)
        {
            double exactMidi = 69 + 12 * Math.Log2(frequencyHz!.Value / 440.0);
            double? cents = target is null ? null : 100 * (exactMidi - target.Midi);
            CurrentError = target is null ? "—" : $"{cents:+0;-0;0} ¢";
            CurrentVoice = $"{NoteCatalog.ToBilingual(NoteCatalog.NearestMidi(frequencyHz.Value))} · {frequencyHz:0.0} Гц";
            _voices.Add(new VoiceSample(seconds, exactMidi, cents));
        }
        else
        {
            CurrentError = "Нет устойчивого тона";
            CurrentVoice = "Голос не распознан";
            _voices.Add(new VoiceSample(seconds, null, null));
        }
        if (_voices.Count > 10000) _voices.RemoveRange(0, _voices.Count - 10000);
        Voice = _voices.ToArray();
        _scorer?.Add(seconds, frequencyHz, confidence);
        _log("midi_pitch_sample", new { seconds = Math.Round(seconds, 3),
            lap = _lastLoop + 1, targetIndex = target?.Index, targetMidi = target?.Midi, frequencyHz, confidence, rms, deviationCents = reliable && target is not null
                ? 1200 * Math.Log2(frequencyHz!.Value / NoteCatalog.ToFrequency(target.Midi)) : (double?)null });
    }

    private bool UpdatePracticePosition(double elapsed)
    {
        if (elapsed >= Duration * LoopCount) { Cursor = Duration; Finish(false); return false; }
        int lap = (int)(elapsed / Duration);
        if (lap != _lastLoop)
        {
            _lastLoop = lap;
            OnChanged(nameof(DisplayedLoop));
            _voices.Clear(); Voice = [];
            _practiceSoundingIndex = null;
            if (SingWithReference) _stopTone();
        }
        double seconds = elapsed - lap * Duration;
        Cursor = seconds;
        var target = MelodyPlanner.At(Targets, seconds);
        CurrentTarget = target is null ? "ПАУЗА В МЕЛОДИИ" : NoteCatalog.ToBilingual(target.Midi);
        return true;
    }

    private async Task RunPracticeTimelineAsync(PausableClock clock, CancellationTokenSource cancel)
    {
        using (cancel)
        {
            try
            {
                while (IsPracticing)
                {
                    cancel.Token.ThrowIfCancellationRequested();
                    if (!IsPaused)
                    {
                        if (!UpdatePracticePosition(clock.ElapsedSeconds)) break;
                        var target = MelodyPlanner.At(Targets, Cursor);
                        if (SingWithReference && target?.Index != _practiceSoundingIndex)
                        {
                            _stopTone(); _practiceSoundingIndex = target?.Index;
                            if (target is not null) _play(NoteCatalog.ToFrequency(target.Midi), Math.Max(.01, target.EndSeconds - Cursor));
                        }
                    }
                    await Task.Delay(25, cancel.Token);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { Finish(true); _error("Практика с эталоном", ex); }
            finally
            {
                if (ReferenceEquals(_practiceTimelineCancel, cancel)) { _practiceTimelineCancel = null; _stopTone(); }
            }
        }
    }

    private void Finish(bool stopped)
    {
        if (!IsPracticing) return;
        var timeline = _practiceTimelineCancel;
        _practiceTimelineCancel = null;
        timeline?.Cancel();
        _stopTone(); _practiceSoundingIndex = null;
        _clock?.Stop(); _clock = null;
        IsPracticing = false;
        IsPaused = false;
        var result = _scorer?.Finish(includeUnobserved: !_scoringAfterSeek); _scorer = null;
        if (result is not null)
        {
            Summary = (_scoringAfterSeek ? "После последнего перехода: " : "") + $"{result.Passed}/{result.NoteCount} нот зачтено · средняя точность {result.AverageAccuracy:0.#}% · покрытие голосом {result.AverageCoverage:0.#}%";
            _log("midi_practice_end", new { status = stopped ? "stopped" : "completed",
                result.NoteCount, result.Passed, result.AverageAccuracy, result.AverageCoverage,
                loops = LoopCount, singWithReference = SingWithReference, scoringAfterSeek = _scoringAfterSeek, notes = result.Notes.Select(n => new { n.Index, n.Midi, note = NoteCatalog.ToLatin(n.Midi),
                    n.Seconds, n.Samples, n.Voiced, n.InTolerance, n.MeanAbsoluteCents, n.Passed }).ToArray() });
        }
        Status = stopped ? "Практика остановлена. Частичный результат сохранён." : "Практика завершена. Результат сохранён в JSONL.";
        Refresh();
    }

    public void TogglePlaybackPause()
    {
        if (_disposed) return;
        if (!IsBusy)
        {
            if (PracticeCommand.CanExecute(null)) PracticeCommand.Execute(null);
            else if (PreviewCommand.CanExecute(null)) PreviewCommand.Execute(null);
            return;
        }
        if (IsPaused)
        {
            _clock?.Resume(); _previewClock?.Resume(); _countClock?.Resume();
            _previewSoundingIndex = null; // recreate the reference tone from the resumed timeline
            _practiceSoundingIndex = null;
            IsPaused = false;
            Status = "Продолжено.";
            _log("midi_resumed", new { IsPracticing, IsPreviewing, IsCountingIn, Cursor });
        }
        else
        {
            _clock?.Pause(); _previewClock?.Pause(); _countClock?.Pause();
            _stopTone(); _previewSoundingIndex = null;
            _practiceSoundingIndex = null;
            IsPaused = true;
            Status = "Пауза. Время не учитывается в оценке.";
            _log("midi_paused", new { IsPracticing, IsPreviewing, IsCountingIn, Cursor });
        }
    }

    public void Repeat() => _ = RepeatAsync();

    private async Task RepeatAsync()
    {
        if (_disposed) return;
        if (!IsBusy)
        {
            TogglePlaybackPause();
            return;
        }
        bool preview = IsPreviewing;
        Stop();
        // The preview loop unwinds asynchronously; don't start a second playback concurrently.
        for (int tries = 0; IsBusy && tries < 100; tries++) await Task.Delay(25);
        if (IsBusy) return;
        if (preview && PreviewCommand.CanExecute(null)) PreviewCommand.Execute(null);
        else if (!preview && PracticeCommand.CanExecute(null)) PracticeCommand.Execute(null);
    }

    public void Stop()
    {
        _previewCancel?.Cancel();
        _countInCancel?.Cancel();
        if (IsPracticing) Finish(true);
        _stopTone();
        _previewSoundingIndex = null;
        if (!IsPreviewing && !IsCountingIn) IsPaused = false;
    }
    public void StopForMicrophone() => Stop();
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
        Refresh();
    }
}
