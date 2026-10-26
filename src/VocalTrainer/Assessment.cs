namespace VocalTrainer;

/// <summary>Configurable per-attempt scoring. Missing/unreliable frames count as time not on target.</summary>
public readonly record struct AssessmentSettings(double ToleranceCents, double RequiredAccuracy, double MinimumConfidence = 0.65)
{
    public void Validate()
    {
        if (!double.IsFinite(ToleranceCents) || ToleranceCents is < 10 or > 100)
            throw new ArgumentOutOfRangeException(nameof(ToleranceCents));
        if (!double.IsFinite(RequiredAccuracy) || RequiredAccuracy is < 0.1 or > 1)
            throw new ArgumentOutOfRangeException(nameof(RequiredAccuracy));
    }
}

public sealed record AssessmentResult(
    bool Success, int Frames, int ReliableFrames, int AccurateFrames,
    double AccuratePercent, double VoiceCoveragePercent, double OnTargetTimePercent,
    double? InitialDeviationCents, double? MedianAbsoluteCents,
    double? MeanAbsoluteCents, double? TimeToLockSeconds,
    double LongestStableSeconds, double? DriftCents,
    double ToleranceCents, double RequiredAccuracy)
{
    public string Description => Frames == 0
        ? "Нет данных для оценки"
        : $"{(Success ? "ЗАЧЁТ" : "ПОВТОР")}: ±{ToleranceCents:0}¢ — {AccuratePercent:0}% среди распознанных, " +
          $"озвучено {VoiceCoveragePercent:0}%, в цели за всё время {OnTargetTimePercent:0}%, средняя ошибка " +
          $"{(MeanAbsoluteCents is null ? "—" : $"{MeanAbsoluteCents:0}¢")}; " +
          $"удержание {LongestStableSeconds:0.0} с";
}

/// <summary>
/// All timestamps come from one monotonic Stopwatch (session elapsed milliseconds).
/// A pitch lock requires three consecutive good frames to avoid one-sample false positives.
/// Scoring is descriptive training feedback, not a medical or vocal-health assessment.
/// </summary>
public sealed class AttemptAssessment
{
    private readonly int _targetMidi;
    private readonly AssessmentSettings _settings;
    private readonly long _startMs;
    private readonly List<double> _errors = [];
    private int _frames, _reliable, _accurate, _stableFrames;
    private double _longestStableMs;
    private long? _streakStartMs, _lockMs, _lastGoodMs;
    private double? _firstError, _firstReliableCents, _lastReliableCents;
    private long _lastObservationMs = -1;

    public AttemptAssessment(int targetMidi, AssessmentSettings settings, long startMs)
    {
        _targetMidi = targetMidi;
        _settings = settings;
        _settings.Validate();
        _startMs = startMs;
    }

    public void Add(PitchMeasurement measurement, long timestampMs)
    {
        if (timestampMs <= _lastObservationMs || timestampMs < _startMs) return;
        _lastObservationMs = timestampMs;
        _frames++;
        bool reliable = measurement.FrequencyHz is { } hz &&
                        measurement.Confidence >= _settings.MinimumConfidence &&
                        double.IsFinite(hz) && hz > 0;
        double? cents = reliable ? NoteCatalog.Cents(measurement.FrequencyHz!.Value, NoteCatalog.ToFrequency(_targetMidi)) : null;
        if (cents is { } error)
        {
            _reliable++;
            _errors.Add(Math.Abs(error));
            _firstError ??= error;
            _firstReliableCents ??= error;
            _lastReliableCents = error;
        }
        if (cents is { } c && Math.Abs(c) <= _settings.ToleranceCents)
        {
            _accurate++;
            if (_streakStartMs is null) _streakStartMs = timestampMs;
            _stableFrames++;
            _lastGoodMs = timestampMs;
            if (_stableFrames >= 3) _lockMs ??= _streakStartMs;
        }
        else
        {
            FlushStable();
        }
    }

    private void FlushStable()
    {
        if (_streakStartMs is { } start && _lastGoodMs is { } last)
            _longestStableMs = Math.Max(_longestStableMs, Math.Max(0, last - start));
        _streakStartMs = _lastGoodMs = null;
        _stableFrames = 0;
    }

    public AssessmentResult Finish()
    {
        FlushStable();
        double percent = _reliable == 0 ? 0 : 100.0 * _accurate / _reliable;
        double coverage = _frames == 0 ? 0 : 100.0 * _reliable / _frames;
        double onTargetTime = _frames == 0 ? 0 : 100.0 * _accurate / _frames;
        bool success = _frames >= 3 && percent >= _settings.RequiredAccuracy * 100 && coverage >= 55;
        var sorted = _errors.OrderBy(e => e).ToArray();
        double? median = sorted.Length == 0 ? null : sorted.Length % 2 == 1 ? sorted[sorted.Length / 2] :
            (sorted[sorted.Length / 2 - 1] + sorted[sorted.Length / 2]) / 2;
        return new AssessmentResult(
            success, _frames, _reliable, _accurate,
            Math.Round(percent, 1), Math.Round(coverage, 1), Math.Round(onTargetTime, 1),
            _firstError is { } d ? Math.Round(d, 1) : null,
            median is { } m ? Math.Round(m, 1) : null,
            _errors.Count == 0 ? null : Math.Round(_errors.Average(), 1),
            _lockMs is { } lockAt ? Math.Round((lockAt - _startMs) / 1000.0, 2) : null,
            Math.Round(_longestStableMs / 1000.0, 2),
            _firstReliableCents is { } first && _lastReliableCents is { } last ? Math.Round(last - first, 1) : null,
            _settings.ToleranceCents, _settings.RequiredAccuracy);
    }
}
