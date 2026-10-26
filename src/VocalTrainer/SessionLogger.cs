using System.IO;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using VocalTrainer.Practice;

namespace VocalTrainer;

/// <summary>One UTF-8 JSONL file per mic session, containing only pitch features/metadata, never audio.</summary>
public sealed class SessionLogger : IDisposable
{
    private readonly StreamWriter _writer;
    private readonly Stopwatch _watch = Stopwatch.StartNew();
    private readonly List<double> _absoluteErrors = [];
    private int _voiced, _within25, _within50, _observations;
    private int _stepVoiced, _stepWithin25, _stepWithin50, _stepObservations;
    private readonly List<double> _stepAbsoluteErrors = [];
    private string _phase = "manual";
    private bool _finished;
    private int _freeFrames, _freeVoiced, _freeAccurate;
    private double _freeError;
    public string FilePath { get; }
    public long ElapsedMs => _watch.ElapsedMilliseconds;

    public SessionLogger(string directory, int targetMidi, string microphoneName)
    {
        Directory.CreateDirectory(directory);
        FilePath = Path.Combine(directory, $"session-{DateTime.Now:yyyyMMdd-HHmmss-fff}.jsonl");
        _writer = new StreamWriter(FilePath, false, new UTF8Encoding(false)) { AutoFlush = true };
        Event("session_start", new {
            schemaVersion = 2, appVersion = "0.5.1", timestampUtc = DateTimeOffset.UtcNow.ToString("O"),
            microphoneName, sampleRate = PitchDetector.SampleRate, analysisWindowSamples = PitchDetector.FrameSamples,
            detector = "YIN-like CMND", calibrationHz = 440.0, initialTargetMidi = targetMidi,
            noteConvention = "C4=261.63Hz=До первой октавы", audioStoredInLog = false,
            centsTolerance = new[] { 25, 50 }
        });
    }

    public void Event(string eventType, object? details = null)
    {
        if (_finished) return;
        _writer.WriteLine(JsonSerializer.Serialize(new { type = eventType, elapsedMs = ElapsedMs, details }));
    }

    public void TargetChanged(int midi)
        => Event("target_change", new { targetMidi = midi, targetNote = NoteCatalog.ToLatin(midi), targetHz = NoteCatalog.ToFrequency(midi) });

    public void SetPhase(string phase, object? details = null)
    {
        _phase = phase;
        Event("phase_change", new { phase, details });
    }

    public void BeginStep(int sectionIndex, int noteIndex, int targetMidi, ExerciseNote step)
    {
        _stepObservations = _stepVoiced = _stepWithin25 = _stepWithin50 = 0;
        _stepAbsoluteErrors.Clear();
        Event("exercise_step_start", new
        {
            sectionIndex, noteIndex, targetMidi, targetNote = NoteCatalog.ToLatin(targetMidi),
            step.PreviewSeconds, step.HoldSeconds, step.TransitionSeconds
        });
    }

    public void EndStep(int sectionIndex, int noteIndex, int targetMidi)
        => Event("exercise_step_end", new
        {
            sectionIndex, noteIndex, targetMidi, observations = _stepObservations,
            voiced = _stepVoiced, within25 = _stepWithin25, within50 = _stepWithin50,
            within25Percent = Ratio(_stepWithin25, _stepObservations),
            within50Percent = Ratio(_stepWithin50, _stepObservations),
            medianAbsoluteCents = Median(_stepAbsoluteErrors)
        });

    public void Sample(int midi, PitchMeasurement pitch)
    {
        if (_finished) return;
        double targetHz = NoteCatalog.ToFrequency(midi);
        double? cents = pitch.FrequencyHz is { } hz ? NoteCatalog.Cents(hz, targetHz) : null;
        if (pitch.Confidence < 0.65) cents = null;
        bool evaluate = _phase is "manual" or "hold" or "sequence_hold";
        if (evaluate)
        {
            _observations++;
            if (_phase == "hold") _stepObservations++;
            if (cents is { } c)
            {
                _voiced++;
                double error = Math.Abs(c);
                _absoluteErrors.Add(error);
                if (error <= 25) _within25++;
                if (error <= 50) _within50++;
                if (_phase == "hold")
                {
                    _stepVoiced++;
                    _stepAbsoluteErrors.Add(error);
                    if (error <= 25) _stepWithin25++;
                    if (error <= 50) _stepWithin50++;
                }
            }
        }
        // No samples from microphone are persisted; only numerical features.
        _writer.WriteLine(JsonSerializer.Serialize(new
        {
            type = "pitch_sample", elapsedMs = ElapsedMs, phase = _phase, targetMidi = midi,
            targetHz = Math.Round(targetHz, 3),
            detectedHz = pitch.FrequencyHz is { } f ? Math.Round(f, 3) : (double?)null,
            cents = cents is { } deviation ? Math.Round(deviation, 1) : (double?)null,
            rms = Math.Round(pitch.Rms, 5), confidence = Math.Round(pitch.Confidence, 3),
            reliable = cents.HasValue, note = cents.HasValue ? NoteCatalog.ToLatin(NoteCatalog.NearestMidi(pitch.FrequencyHz!.Value)) : null
        }));
    }

    public string Finish()
    {
        if (_finished) return "Занятие завершено";
        string result = _observations == 0 ? (_freeVoiced == 0 ? "Нет оценённых звуков" :
            $"Свободное пение: ±25 ¢ от ближайшей ноты — {Ratio(_freeAccurate, _freeVoiced):0.#}%, среднее отклонение {_freeError / _freeVoiced:0.#} ¢") :
            $"±25¢: {_within25}/{_observations} ({Ratio(_within25, _observations):0.#}%). " +
            $"Медианная ошибка: {Median(_absoluteErrors):0.#}¢";
        Event("session_end", new
        {
            observations = _observations, voiced = _voiced, within25 = _within25, within50 = _within50,
            within25Percent = Ratio(_within25, _observations), within50Percent = Ratio(_within50, _observations),
            medianAbsoluteCents = Median(_absoluteErrors),
            freeSinging = new { frames = _freeFrames, voiced = _freeVoiced, within25 = _freeAccurate,
                meanAbsoluteCents = _freeVoiced == 0 ? (double?)null : _freeError / _freeVoiced }
        });
        _finished = true;
        _writer.Dispose();
        return result;
    }

    private static double? Ratio(int numerator, int denominator)
        => denominator == 0 ? null : Math.Round(100.0 * numerator / denominator, 1);
    private static double? Median(List<double> values)
    {
        if (values.Count == 0) return null;
        var sorted = values.OrderBy(x => x).ToArray();
        int n = sorted.Length;
        return Math.Round(n % 2 == 1 ? sorted[n / 2] : (sorted[n / 2 - 1] + sorted[n / 2]) / 2, 1);
    }
    public void Dispose() => Finish();

    public void FreeSample(FreePitchFrame frame, PitchMeasurement measurement)
    {
        if (_finished) return;
        _freeFrames++;
        if (frame.Reliable)
        {
            _freeVoiced++; double error = Math.Abs(frame.DeviationCents!.Value); _freeError += error;
            if (error <= 25) _freeAccurate++;
        }
        Event("free_pitch_sample", new { freeElapsedMs = frame.Milliseconds, frame.FrequencyHz, frame.Midi, frame.MidiPitch,
            detectedFrequencyHz = measurement.FrequencyHz is { } detected && double.IsFinite(detected) ? detected : (double?)null,
            note = frame.Midi is { } midi ? NoteCatalog.ToLatin(midi) : null,
            octave = frame.Midi is { } m ? NoteCatalog.ToOctaveName(m / 12 - 1) : null,
            nearestNoteHz = frame.Midi is { } n ? NoteCatalog.ToFrequency(n) : (double?)null,
            frame.DeviationCents, frame.MovementCents, frame.CentsPerSecond, frame.Direction, frame.Reliable,
            rms = double.IsFinite(measurement.Rms) ? measurement.Rms : (double?)null,
            confidence = double.IsFinite(measurement.Confidence) ? measurement.Confidence : (double?)null });
    }
}
