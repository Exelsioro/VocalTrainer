namespace VocalTrainer.Practice;

public sealed record StudyNoteOption(int Midi, string Display)
{
    public override string ToString() => Display;
    public static IReadOnlyList<StudyNoteOption> Make(int firstMidi, int count)
        => Enumerable.Range(firstMidi, count)
            .Select(midi => new StudyNoteOption(midi, NoteCatalog.ToBilingual(midi)))
            .ToArray();
}
