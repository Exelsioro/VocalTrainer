namespace VocalTrainer.Melody;

public sealed record TimedTarget(int Index, int Midi, double StartSeconds, double DurationSeconds)
{
    public double EndSeconds => StartSeconds + DurationSeconds;
}
public sealed record VoiceSample(double Seconds, double? MidiPitch, double? DeviationCents);

/// <summary>Plot data independent of WPF. Missing observations break the trace, even at the same pitch.</summary>
public static class VoicePlot
{
    public static (int Minimum, int Maximum) PitchBounds(IReadOnlyList<TimedTarget> targets, IReadOnlyList<VoiceSample> voice)
    {
        var pitches = targets.Select(n => (double)n.Midi)
            .Concat(voice.Where(p => p.MidiPitch is { } midi && double.IsFinite(midi) && midi is >= 0 and <= 127)
                .Select(p => p.MidiPitch!.Value)).ToArray();
        return pitches.Length == 0 ? (45, 69) :
            (Math.Max(0, (int)Math.Floor(pitches.Min()) - 2), Math.Min(127, (int)Math.Ceiling(pitches.Max()) + 2));
    }

    public static IReadOnlyList<IReadOnlyList<VoiceSample>> Strokes(IReadOnlyList<VoiceSample> voice, double from, double to)
    {
        var strokes = new List<IReadOnlyList<VoiceSample>>();
        List<VoiceSample>? current = null;
        foreach (var point in voice)
        {
            if (!double.IsFinite(point.Seconds) || point.Seconds < from || point.Seconds > to ||
                point.MidiPitch is not { } midi || !double.IsFinite(midi) || midi is < 0 or > 127)
            { current = null; continue; }
            if (current is null || point.Seconds <= current[^1].Seconds || point.Seconds - current[^1].Seconds > .35)
            {
                current = [];
                strokes.Add(current);
            }
            current.Add(point);
        }
        return strokes;
    }
}

/// <summary>Turns a selected MIDI track into a monophonic singing guide. If notes overlap, highest note wins.</summary>
public static class MelodyPlanner
{
    public static IReadOnlyList<TimedTarget> Build(MidiTrack track, double speed, int semitones,
        double loopStartSeconds, double loopEndSeconds)
    {
        if (speed is < .5 or > 1.5 || !double.IsFinite(speed)) throw new ArgumentOutOfRangeException(nameof(speed));
        if (semitones is < -127 or > 127) throw new ArgumentOutOfRangeException(nameof(semitones));
        if (track.Notes.Any(n => n.Midi + semitones is < 0 or > 127))
            throw new ArgumentException("Сдвиг всей партии выходит за пределы MIDI 0–127. Уменьши транспозицию.");
        if (!double.IsFinite(loopStartSeconds) || !double.IsFinite(loopEndSeconds) ||
            loopStartSeconds < 0 || loopEndSeconds <= loopStartSeconds ||
            loopEndSeconds > track.EndSeconds + .001) throw new ArgumentException("Неверные границы фрагмента.");

        // Break at every note-on/off. This gives one unambiguous pitch target at a time.
        var inside = track.Notes.Where(n => n.EndSeconds > loopStartSeconds && n.StartSeconds < loopEndSeconds).ToArray();
        var edges = new SortedSet<double> { loopStartSeconds, loopEndSeconds };
        foreach (var note in inside)
        {
            edges.Add(Math.Max(loopStartSeconds, note.StartSeconds));
            edges.Add(Math.Min(loopEndSeconds, note.EndSeconds));
        }
        var points = edges.ToArray();
        var slices = new List<TimedTarget>();
        MidiNote? previousNote = null;
        for (int i = 0; i < points.Length - 1; i++)
        {
            double a = points[i], b = points[i + 1];
            if (b <= a) continue;
            var active = inside.Where(n => n.StartSeconds <= a + .000001 && n.EndSeconds >= b - .000001)
                .OrderByDescending(n => n.Midi).FirstOrDefault();
            if (active is null) continue;
            int pitch = active.Midi + semitones;
            if (pitch is < 0 or > 127) throw new ArgumentException("Транспонирование выводит ноту за пределы MIDI 0–127.");
            double start = (a - loopStartSeconds) / speed;
            double duration = (b - a) / speed;
            if (slices.Count > 0 && ReferenceEquals(previousNote, active) && slices[^1].Midi == pitch &&
                Math.Abs(slices[^1].EndSeconds - start) < .00001)
            {
                var last = slices[^1];
                slices[^1] = last with { DurationSeconds = last.DurationSeconds + duration };
            }
            else slices.Add(new TimedTarget(slices.Count, pitch, start, duration));
            previousNote = active;
        }
        return slices;
    }
    public static TimedTarget? At(IReadOnlyList<TimedTarget> targets, double seconds)
        => targets.FirstOrDefault(n => seconds >= n.StartSeconds && seconds < n.EndSeconds);
}

public sealed record MelodyNoteResult(int Index, int Midi, double Seconds, int Samples, int Voiced,
    int InTolerance, double? MeanAbsoluteCents, bool Passed);
public sealed record MelodyResult(int NoteCount, int Passed, double AverageAccuracy,
    double AverageCoverage, IReadOnlyList<MelodyNoteResult> Notes);

/// <summary>One pitch observation per UI tick; silence counts against coverage, not against pitch accuracy.</summary>
public sealed class MelodyScorer(IReadOnlyList<TimedTarget> targets, int toleranceCents = 40)
{
    private readonly Dictionary<int, (int Count, int Voiced, int Accurate, double Error)> _data = new();
    public void Add(double seconds, double? frequencyHz, double confidence)
    {
        var target = MelodyPlanner.At(targets, seconds);
        if (target is null) return;
        var d = _data.GetValueOrDefault(target.Index);
        d.Count++;
        if (frequencyHz is > 0 && double.IsFinite(frequencyHz.Value) &&
            double.IsFinite(confidence) && confidence >= .65)
        {
            d.Voiced++;
            double error = Math.Abs(1200 * Math.Log2(frequencyHz.Value /
                (440 * Math.Pow(2, (target.Midi - 69) / 12.0))));
            d.Error += error;
            if (error <= toleranceCents) d.Accurate++;
        }
        _data[target.Index] = d;
    }
    public MelodyResult Finish(bool includeUnobserved = true)
    {
        var notes = targets.Where(n => includeUnobserved || _data.GetValueOrDefault(n.Index).Count > 0).Select(n =>
        {
            var d = _data.GetValueOrDefault(n.Index);
            bool passed = d.Count > 0 && d.Voiced >= 2 &&
                (double)d.Voiced / d.Count >= .55 && (double)d.Accurate / d.Voiced >= .75;
            return new MelodyNoteResult(n.Index, n.Midi, n.DurationSeconds, d.Count,
                d.Voiced, d.Accurate, d.Voiced == 0 ? null : d.Error / d.Voiced, passed);
        }).ToArray();
        return new MelodyResult(notes.Length, notes.Count(n => n.Passed),
            notes.Length == 0 ? 0 : notes.Average(n => n.Voiced == 0 ? 0 : 100.0 * n.InTolerance / n.Voiced),
            notes.Length == 0 ? 0 : notes.Average(n => n.Samples == 0 ? 0 : 100.0 * n.Voiced / n.Samples), notes);
    }
}
