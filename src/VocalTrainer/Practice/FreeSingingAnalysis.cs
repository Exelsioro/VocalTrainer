namespace VocalTrainer.Practice;

public sealed record FreePitchFrame(long Milliseconds, double? FrequencyHz, int? Midi, double? MidiPitch,
    double? DeviationCents, double? MovementCents, double? CentsPerSecond, string Direction)
{
    public bool Reliable => Midi.HasValue;
}

/// <summary>Describes singing against the nearest equal-tempered note, without choosing a target melody.</summary>
public sealed class FreeSingingAnalysis
{
    private FreePitchFrame? _previous;
    public FreePitchFrame Analyze(PitchMeasurement measurement, long milliseconds)
    {
        if (measurement.FrequencyHz is not { } hz || !double.IsFinite(hz) || hz <= 0 ||
            !double.IsFinite(measurement.Confidence) || measurement.Confidence < .65)
        {
            _previous = null;
            return new(milliseconds, null, null, null, null, null, null, "Нет устойчивого тона");
        }
        double pitch = 69 + 12 * Math.Log2(hz / 440);
        if (!double.IsFinite(pitch) || pitch is < 0 or > 127)
        {
            _previous = null;
            return new(milliseconds, null, null, null, null, null, null, "Вне диапазона нот");
        }
        int midi = NoteCatalog.NearestMidi(hz);
        double? movement = null, speed = null;
        if (_previous is { MidiPitch: { } previousPitch } previous && milliseconds > previous.Milliseconds && milliseconds - previous.Milliseconds <= 500)
        {
            movement = 100 * (pitch - previousPitch);
            speed = movement * 1000 / (milliseconds - previous.Milliseconds);
        }
        string direction = movement is null ? "Новый звук" : movement > 15 ? "↑ Вверх" : movement < -15 ? "↓ Вниз" : "→ Удержание";
        return _previous = new(milliseconds, hz, midi, pitch, 100 * (pitch - midi), movement, speed, direction);
    }
}
