using VocalTrainer.Infrastructure;
using VocalTrainer.Practice;

namespace VocalTrainer.ViewModels;

public sealed class EarTrainingViewModel : ObservableObject, IDisposable
{
    private readonly Action<double, double> _playTone;
    private readonly Action _stopTone;
    private readonly Action<string, object> _log;
    private readonly Func<bool> _available;
    private readonly Random _random = new();
    private CancellationTokenSource? _playCancellation;
    private EarChallenge? _challenge;
    private int _centerMidi = 48;
    private int _interval = 2;
    private bool _includeSame = true;
    private bool _adaptive = true;
    private int _correctStreak, _incorrectStreak;
    private string _status = "Слушай две ноты и определи, как изменилась высота.";
    private string _answerText = "Нет задания";
    private string _scoreText = "Правильных ответов: 0 / 0";
    private int _correct;
    private int _total;
    private bool _isPlaying;
    private bool _canAnswer;
    private bool _answered;
    private bool _disposed;

    public IReadOnlyList<StudyNoteOption> NoteChoices { get; } = StudyNoteOption.Make(36, 49);
    private StudyNoteOption? _selectedCenter;
    public StudyNoteOption? SelectedCenter
    {
        get => _selectedCenter;
        set { if (Set(ref _selectedCenter, value) && value is not null) CenterMidi = value.Midi; }
    }
    public IReadOnlyList<int> Intervals { get; } = [1, 2, 3, 4, 5, 7, 12];
    public int CenterMidi { get => _centerMidi; set => Set(ref _centerMidi, value); }
    public int Interval { get => _interval; set => Set(ref _interval, value); }
    public bool IncludeSame { get => _includeSame; set => Set(ref _includeSame, value); }
    public bool Adaptive { get => _adaptive; set => Set(ref _adaptive, value); }
    public string Status { get => _status; private set => Set(ref _status, value); }
    public string AnswerText { get => _answerText; private set => Set(ref _answerText, value); }
    public string ScoreText { get => _scoreText; private set => Set(ref _scoreText, value); }
    public bool IsPlaying { get => _isPlaying; private set { if (Set(ref _isPlaying, value)) Refresh(); } }
    public bool CanAnswer { get => _canAnswer; private set { if (Set(ref _canAnswer, value)) Refresh(); } }
    public bool CanStart => !_disposed && !_isPlaying && _available();

    public RelayCommand NewChallengeCommand { get; }
    public RelayCommand ReplayCommand { get; }
    public RelayCommand HigherCommand { get; }
    public RelayCommand LowerCommand { get; }
    public RelayCommand SameCommand { get; }

    public EarTrainingViewModel(Action<double, double> playTone, Action stopTone,
        Action<string, object> log, Func<bool> available)
    {
        _playTone = playTone;
        _stopTone = stopTone;
        _log = log;
        _available = available;
        SelectedCenter = NoteChoices.Single(x => x.Midi == 48);
        NewChallengeCommand = new RelayCommand(() => _ = NewChallengeAsync(), () => CanStart);
        ReplayCommand = new RelayCommand(() => _ = PlayAsync(), () => CanStart && _challenge is not null && !_answered);
        HigherCommand = new RelayCommand(() => Answer(PitchDirection.Higher), () => CanAnswer);
        LowerCommand = new RelayCommand(() => Answer(PitchDirection.Lower), () => CanAnswer);
        SameCommand = new RelayCommand(() => Answer(PitchDirection.Same), () => CanAnswer);
    }

    public void Refresh()
    {
        OnChanged(nameof(CanStart));
        NewChallengeCommand.Refresh(); ReplayCommand.Refresh();
        HigherCommand.Refresh(); LowerCommand.Refresh(); SameCommand.Refresh();
    }

    private async Task NewChallengeAsync()
    {
        if (!CanStart) return;
        _challenge = EarTrainingLogic.Create(_random, CenterMidi, Interval, IncludeSame);
        _answered = false;
        CanAnswer = false;
        AnswerText = "?";
        _log("ear_question", new { firstMidi = _challenge.FirstMidi, interval = Interval, includeSame = IncludeSame });
        await PlayAsync();
    }

    private async Task PlayAsync()
    {
        if (!CanStart || _challenge is null || _answered) return;
        using var cts = new CancellationTokenSource();
        _playCancellation = cts;
        IsPlaying = true;
        CanAnswer = false;
        _log("ear_playback_start", new { });
        try
        {
            Status = "Первая нота...";
            _playTone(NoteCatalog.ToFrequency(_challenge.FirstMidi), .85);
            await Task.Delay(1050, cts.Token);
            _stopTone();
            Status = "Пауза...";
            await Task.Delay(450, cts.Token);
            Status = "Вторая нота...";
            _playTone(NoteCatalog.ToFrequency(_challenge.SecondMidi), .85);
            await Task.Delay(950, cts.Token);
            _stopTone();
            CanAnswer = true;
            Status = "Вторая нота выше, ниже или такая же?";
        }
        catch (OperationCanceledException) { Status = "Воспроизведение отменено."; }
        catch (Exception ex) { Status = "Ошибка воспроизведения: " + ex.Message; }
        finally
        {
            _stopTone();
            _playCancellation = null;
            IsPlaying = false;
            _log("ear_playback_end", new { cancelled = cts.IsCancellationRequested });
            Refresh();
        }
    }

    private void Answer(PitchDirection chosen)
    {
        if (!CanAnswer || _challenge is null) return;
        _answered = true;
        bool correct = EarTrainingLogic.Evaluate(_challenge, chosen);
        _total++;
        if (correct) { _correct++; _correctStreak++; _incorrectStreak = 0; }
        else { _incorrectStreak++; _correctStreak = 0; }
        if (Adaptive)
        {
            int index = Array.IndexOf(Intervals.ToArray(), Interval);
            if (index < 0) index = 0;
            if (_correctStreak >= 3)
            {
                Interval = Intervals[Math.Max(0, index - 1)];
                _correctStreak = 0;
            }
            else if (_incorrectStreak >= 2)
            {
                Interval = Intervals[Math.Min(Intervals.Count - 1, index + 1)];
                _incorrectStreak = 0;
            }
        }
        CanAnswer = false;
        AnswerText = $"{(correct ? "Верно" : "Не совпало")} · {NoteCatalog.ToLatin(_challenge.FirstMidi)} → {NoteCatalog.ToLatin(_challenge.SecondMidi)}";
        ScoreText = $"Правильных ответов: {_correct} / {_total} ({100.0 * _correct / _total:0}%)";
        Status = "Нажми «Следующий вопрос» для продолжения.";
        _log("ear_answer", new
        {
            firstMidi = _challenge.FirstMidi, secondMidi = _challenge.SecondMidi,
            chosen = chosen.ToString(), correct, intervalSemitones = Math.Abs(_challenge.SecondMidi - _challenge.FirstMidi), nextInterval = Interval, adaptive = Adaptive
        });
    }

    public void Stop()
    {
        _playCancellation?.Cancel();
        _stopTone();
    }

    public void Dispose()
    {
        _disposed = true;
        Stop();
    }
}
