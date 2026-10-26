using VocalTrainer.Melody;

namespace VocalTrainer.Practice;

public sealed record SequenceNoteResult(int Index, string Note, AssessmentResult Assessment);
public sealed record SequenceResult(IReadOnlyList<SequenceNoteResult> Notes)
{
    public int Passed => Notes.Count(n => n.Assessment.Success);
    public bool Success => Notes.Count > 0 && Passed == Notes.Count;
    public string Description => $"Фраза: {Passed}/{Notes.Count} нот зачтено. " + string.Join(" · ", Notes.Select(n =>
        $"{n.Note}: {(n.Assessment.Frames == 0 ? "нет данных" : $"{n.Assessment.AccuratePercent:0}% точно, голос {n.Assessment.VoiceCoveragePercent:0}%, ошибка {n.Assessment.MeanAbsoluteCents:0} ¢")}"));
}

/// <summary>Fixed phrase timing; silence and missing notes cannot advance the score or earn credit.</summary>
public sealed class SequenceAttempt
{
    public IReadOnlyList<TimedTarget> Targets { get; }
    public double Duration => Targets[^1].EndSeconds;
    private readonly AttemptAssessment[] _assessments;
    public SequenceAttempt(ExerciseSection section, double defaultTolerance, double requiredAccuracy = .75)
    {
        if (section.Notes.Count == 0) throw new ArgumentException("Нужна хотя бы одна нота.");
        var targets = new List<TimedTarget>();
        var assessments = new List<AttemptAssessment>();
        double seconds = 0;
        for (int index = 0; index < section.Notes.Count; index++)
        {
            var note = section.Notes[index];
            int midi = ExerciseIO.ParseMidi(note.Note);
            targets.Add(new(index, midi, seconds, note.HoldSeconds) { ToleranceCents = note.ToleranceCents ?? defaultTolerance });
            assessments.Add(new(midi, new(note.ToleranceCents ?? defaultTolerance, note.RequiredAccuracy ?? requiredAccuracy), (long)(seconds * 1000)));
            seconds += note.HoldSeconds + note.TransitionSeconds;
        }
        Targets = targets; _assessments = assessments.ToArray();
    }
    public TimedTarget? At(double seconds) => MelodyPlanner.At(Targets, seconds);
    public VoiceSample Add(PitchMeasurement pitch, double seconds)
    {
        var target = At(seconds);
        if (target is not null) _assessments[target.Index].Add(pitch, (long)(seconds * 1000));
        double? midiPitch = pitch.FrequencyHz is { } hz && double.IsFinite(hz) && hz > 0 && pitch.Confidence >= .65
            ? 69 + 12 * Math.Log2(hz / 440) : null;
        return new(seconds, midiPitch, target is not null && midiPitch is { } p ? (p - target.Midi) * 100 : null)
            { ToleranceCents = target?.ToleranceCents };
    }
    public SequenceResult Finish() => new(Targets.Select(t => new SequenceNoteResult(t.Index,
        NoteCatalog.ToLatin(t.Midi), _assessments[t.Index].Finish())).ToArray());
}
