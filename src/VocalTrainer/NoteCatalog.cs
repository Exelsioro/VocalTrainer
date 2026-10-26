namespace VocalTrainer;

public sealed record NoteName(string Latin, string Russian)
{
    public string Display => $"{Latin}  —  {Russian}";
}

public sealed record OctaveOption(int Number, string Name)
{
    public string Display => $"{Number} — {Name}";
}

public static class NoteCatalog
{
    public static readonly NoteName[] Names =
    [
        new("C", "До"), new("C♯", "До♯"), new("D", "Ре"), new("D♯", "Ре♯"),
        new("E", "Ми"), new("F", "Фа"), new("F♯", "Фа♯"), new("G", "Соль"),
        new("G♯", "Соль♯"), new("A", "Ля"), new("A♯", "Ля♯"), new("B", "Си")
    ];

    // Scientific pitch notation: C4 = middle C = 261.63 Hz.
    // Russian tradition: C4 = до первой октавы, C3 = до малой октавы.
    private static readonly Dictionary<int, string> RussianOctaves = new()
    {
        [-1] = "субконтроктава (ниже C0)",
        [0] = "субконтроктава", [1] = "контроктава", [2] = "большая октава",
        [3] = "малая октава", [4] = "1-я октава", [5] = "2-я октава",
        [6] = "3-я октава", [7] = "4-я октава", [8] = "5-я октава",
        [9] = "выше 5-й октавы"
    };

    public static int ToMidi(int noteIndex, int octave) => (octave + 1) * 12 + noteIndex;
    public static string ToOctaveName(int octave) => RussianOctaves.GetValueOrDefault(octave, "—");
    public static double ToFrequency(int midi) => 440.0 * Math.Pow(2.0, (midi - 69) / 12.0);
    public static string ToLatin(int midi) => $"{Names[midi % 12].Latin}{midi / 12 - 1}";
    public static string ToRussian(int midi) => $"{Names[midi % 12].Russian} ({ToOctaveName(midi / 12 - 1)})";
    public static string ToBilingual(int midi) => $"{ToLatin(midi)} — {ToRussian(midi)}";
    public static int NearestMidi(double hz) => Math.Clamp((int)Math.Round(69 + 12 * Math.Log2(hz / 440.0)), 0, 127);
    public static double Cents(double hz, double targetHz) => 1200 * Math.Log2(hz / targetHz);
}
