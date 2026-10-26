using VocalTrainer.Controls;
using VocalTrainer.Melody;
using VocalTrainer.Practice;

namespace VocalTrainer.ViewModels;

public sealed partial class MainViewModel
{
    private string _exerciseModeCode = "notes";
    private bool _exercisePlayReference = true;
    public IReadOnlyList<ExerciseModeOption> ExerciseModes => ExerciseModeOption.All;
    public string ExerciseModeCode
    {
        get => _exerciseModeCode;
        set
        {
            if (ExerciseActive || value is not ("notes" or "sequence") || !Set(ref _exerciseModeCode, value)) return;
            OnChanged(nameof(RepeatExerciseText)); OnChanged(nameof(SkipExerciseText));
            UpdateExerciseMeta();
        }
    }
    public bool ExercisePlayReference { get => _exercisePlayReference; set { if (!ExerciseActive && Set(ref _exercisePlayReference, value)) UpdateExerciseMeta(); } }
    public string RepeatExerciseText => ExerciseModeCode == "sequence" ? "↻ Повторить фразу [R]" : "↻ Повторить ноту [R]";
    public string SkipExerciseText => ExerciseModeCode == "sequence" ? "↷ Пропустить фразу [N]" : "↷ Пропустить ноту [N]";
    private readonly Queue<PitchPoint> _exercisePitch = new();
    public IReadOnlyList<PitchPoint> ExercisePitchPoints => FeedbackHidden ? [] : _exercisePitch.ToArray();
    private SequenceAttempt? _sequenceAttempt;
    private double _sequenceStartedSeconds;
    private IReadOnlyList<TimedTarget> _sequenceTargets = [];
    private readonly List<VoiceSample> _sequenceVoice = [];
    private readonly List<string> _sequenceSummaries = [];
    private double _sequenceCursor;
    public IReadOnlyList<TimedTarget> SequenceTargets => _sequenceTargets;
    public IReadOnlyList<VoiceSample> SequenceVoice => FeedbackHidden ? [] : _sequenceVoice.ToArray();
    public double SequenceDuration => _sequenceTargets.Count == 0 ? 1 : _sequenceTargets[^1].EndSeconds;
    public double SequenceCursor { get => _sequenceCursor; private set => Set(ref _sequenceCursor, value); }
    public bool HasSequenceGraph => _sequenceTargets.Count > 0;

    private void UpdateExerciseMeta()
    {
        if (_exercise is not { } doc) return;
        double seconds = doc.Sections.Sum(s => ExerciseModeCode == "sequence"
            ? 3 + new SequenceAttempt(s, SelectedTolerance).Duration * (ExercisePlayReference ? 2 : 1)
            : s.Notes.Sum(n => n.HoldSeconds + n.TransitionSeconds + (ExercisePlayReference ? n.PreviewSeconds : 0)));
        ExerciseMeta = $"{doc.Sections.Count} секц. · {doc.Sections.Sum(s => s.Notes.Count)} нот · {seconds / 60:0.#} мин без повторов";
    }

    private void ClearSequenceGraph()
    {
        _sequenceTargets = []; _sequenceVoice.Clear(); SequenceCursor = 0;
        NotifySequenceGraph();
    }
    private void NotifySequenceGraph()
    {
        OnChanged(nameof(SequenceTargets)); OnChanged(nameof(SequenceVoice));
        OnChanged(nameof(SequenceDuration)); OnChanged(nameof(HasSequenceGraph));
    }
    private void ObserveExerciseGraph(long milliseconds, double? cents)
    {
        _exercisePitch.Enqueue(new(milliseconds, cents));
        while (_exercisePitch.TryPeek(out var oldest) && milliseconds - oldest.Milliseconds > 30000) _exercisePitch.Dequeue();
        OnChanged(nameof(ExercisePitchPoints));
    }
    private void ObserveSequencePitch(PitchMeasurement measurement)
    {
        if (_sequenceAttempt is not { } attempt || _exerciseClock is null) return;
        double seconds = _exerciseClock.ElapsedSeconds - _sequenceStartedSeconds;
        if (seconds < 0 || seconds >= attempt.Duration) return;
        var target = attempt.At(seconds);
        if (target is not null && target.Midi != _targetMidi) SetTarget(target.Midi, true);
        var sample = attempt.Add(measurement, seconds);
        _sequenceVoice.Add(sample); OnChanged(nameof(SequenceVoice));
        _log?.Event("sequence_pitch_sample", new { seconds, targetIndex = target?.Index, targetMidi = target?.Midi,
            midiPitch = sample.MidiPitch, cents = sample.DeviationCents, reliable = sample.MidiPitch.HasValue });
    }
    private void ShowSequenceTarget(TimedTarget? target)
    {
        if (target is not null && target.Midi != _targetMidi) SetTarget(target.Midi, true);
        foreach (var item in Timeline)
        {
            item.Active = target is not null && item.Index == target.Index + 1;
            item.Completed = target is not null && item.Index <= target.Index;
        }
    }
    private async Task RunSequenceSectionAsync(ExerciseSection section, int sectionIndex, int completed, int total, CancellationToken token)
    {
        int limit = AutoRepeatFailed ? SelectedMaxAttempts : 1;
        for (int number = 1; number <= limit; number++)
        {
            token.ThrowIfCancellationRequested(); _skipRequested = _repeatRequested = false;
            var attempt = new SequenceAttempt(section, SelectedTolerance);
            _sequenceTargets = attempt.Targets; _sequenceVoice.Clear(); SequenceCursor = 0; NotifySequenceGraph();
            ExercisePosition = $"Фраза {sectionIndex + 1}: {section.Notes.Count} нот · попытка {number}/{limit}";
            _log?.Event("sequence_attempt_start", new { sectionIndex, attempt = number, playReference = ExercisePlayReference,
                targets = attempt.Targets, duration = attempt.Duration });
            if (ExercisePlayReference)
            {
                _log?.SetPhase("sequence_preview");
                foreach (var target in attempt.Targets)
                {
                    token.ThrowIfCancellationRequested();
                    if (_skipRequested || _repeatRequested) break;
                    ShowSequenceTarget(target); SequenceCursor = target.StartSeconds;
                    _audio.PlayTone(NoteCatalog.ToFrequency(target.Midi), target.DurationSeconds);
                    await RunPhaseAsync("СЛУШАЙ ЭТАЛОН", target.DurationSeconds, completed, total, token);
                    _audio.StopTone();
                    if (_skipRequested || _repeatRequested) break;
                    if (target.Index + 1 < section.Notes.Count)
                        await RunPhaseAsync("ПАУЗА ЭТАЛОНА", section.Notes[target.Index].TransitionSeconds, completed, total, token);
                }
            }
            if (!_skipRequested && !_repeatRequested)
            {
                _log?.SetPhase("sequence_prepare");
                ShowSequenceTarget(null); SequenceCursor = 0;
                await RunPhaseAsync("ПРИГОТОВЬСЯ", 3, completed, total, token);
            }
            if (!_skipRequested && !_repeatRequested)
            {
                _log?.SetPhase("sequence_hold");
                _sequenceStartedSeconds = _exerciseClock!.ElapsedSeconds;
                _sequenceAttempt = attempt;
                await RunPhaseAsync("ПОВТОРИ ФРАЗУ", attempt.Duration, completed, total, token, () =>
                {
                    SequenceCursor = Math.Min(attempt.Duration, _exerciseClock.ElapsedSeconds - _sequenceStartedSeconds);
                    ShowSequenceTarget(attempt.At(SequenceCursor));
                });
                _sequenceAttempt = null;
            }
            token.ThrowIfCancellationRequested();
            if (_skipRequested) { _log?.Event("sequence_skipped", new { sectionIndex, attempt = number }); break; }
            if (_repeatRequested) { _log?.Event("sequence_repeated", new { sectionIndex, attempt = number }); number--; continue; }
            var result = attempt.Finish();
            _sequenceSummaries.Add($"Секция {sectionIndex + 1}, попытка {number}: {result.Description}");
            _log?.Event("sequence_attempt_assessment", new { sectionIndex, attempt = number, result });
            foreach (var note in result.Notes)
            {
                _exerciseResults.Add(note.Assessment); _evaluatedSteps++;
                if (note.Assessment.Success) _successfulSteps++;
            }
            if (SelectedFeedbackMode?.Code != "test") LatestAssessmentText = result.Description;
            SequenceCursor = attempt.Duration;
            if (result.Success || number == limit) break;
            _log?.SetPhase("transition");
            await RunPhaseAsync("ОТДЫХ / ПОВТОР ФРАЗЫ", section.Notes[^1].TransitionSeconds, completed, total, token);
            if (_skipRequested) break;
            if (_repeatRequested) number--;
        }
        _sequenceAttempt = null;
        foreach (var item in Timeline) { item.Active = false; item.Completed = true; }
    }
}
