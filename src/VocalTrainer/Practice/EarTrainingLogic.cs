namespace VocalTrainer.Practice;

public enum PitchDirection { Lower = -1, Same = 0, Higher = 1 }

public sealed record EarChallenge(int FirstMidi, int SecondMidi)
{
    public PitchDirection Direction => SecondMidi > FirstMidi ? PitchDirection.Higher
        : SecondMidi < FirstMidi ? PitchDirection.Lower : PitchDirection.Same;
}

/// <summary>Generates comparisons without leaving the allowed MIDI range.</summary>
public static class EarTrainingLogic
{
    public static EarChallenge Create(Random random, int centerMidi, int interval, bool includeSame)
    {
        ArgumentNullException.ThrowIfNull(random);
        if (centerMidi is < 0 or > 127) throw new ArgumentOutOfRangeException(nameof(centerMidi));
        if (interval is < 1 or > 24) throw new ArgumentOutOfRangeException(nameof(interval));
        int[] directions = includeSame ? [-1, 0, 1] : [-1, 1];
        var possible = directions.Where(d => centerMidi + d * interval is >= 0 and <= 127).ToArray();
        if (possible.Length == 0) throw new ArgumentOutOfRangeException(nameof(centerMidi));
        int direction = possible[random.Next(possible.Length)];
        return new EarChallenge(centerMidi, centerMidi + direction * interval);
    }

    public static bool Evaluate(EarChallenge challenge, PitchDirection chosen) => challenge.Direction == chosen;
}
