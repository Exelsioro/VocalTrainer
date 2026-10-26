using VocalTrainer.Infrastructure;
using VocalTrainer.Melody;
using VocalTrainer.Practice;

namespace VocalTrainer.ViewModels;

public sealed class FreeSingingViewModel : ObservableObject
{
    private FreeSingingAnalysis _analysis = new();
    private readonly Queue<VoiceSample> _history = new();
    private int _frames, _voiced, _accurate;
    private double _absoluteError;
    private int? _minMidi, _maxMidi;
    private FreePitchFrame _current = new(0, null, null, null, null, null, null, "Ожидание голоса");
    private IReadOnlyList<VoiceSample> _voice = [];
    public FreePitchFrame Current { get => _current; private set => Set(ref _current, value); }
    public IReadOnlyList<VoiceSample> Voice { get => _voice; private set => Set(ref _voice, value); }
    public string Note => Current.Midi is { } midi ? NoteCatalog.ToLatin(midi) : "—";
    public string Octave => Current.Midi is { } midi ? NoteCatalog.ToRussian(midi) : "Нет устойчивой ноты";
    public string Frequency => Current.FrequencyHz is { } hz ? $"{hz:0.0} Гц" : "—";
    public string Movement => Current.Direction + (Current.MovementCents is { } cents ? $" · {cents:+0;-0;0} ¢" : "");
    public string Intonation => Current.DeviationCents is { } cents ? $"{cents:+0;-0;0} ¢ от ближайшей ноты" : "Нет данных о высоте";
    public double Duration => Math.Max(30, Current.Milliseconds / 1000.0);
    public double ViewStart => Math.Max(0, Duration - 30);
    public double Cursor => Current.Milliseconds / 1000.0;
    public string Summary => _voiced == 0 ? "Ожидание достоверного голоса" :
        $"Диапазон {NoteCatalog.ToLatin(_minMidi!.Value)}–{NoteCatalog.ToLatin(_maxMidi!.Value)} · " +
        $"±25 ¢ от ближайшей ноты: {100.0 * _accurate / _voiced:0.#}% · распознано {100.0 * _voiced / _frames:0.#}% · среднее отклонение {_absoluteError / _voiced:0.#} ¢";
    public object Statistics => new { frames = _frames, voiced = _voiced, within25 = _accurate,
        minimumMidi = _minMidi, maximumMidi = _maxMidi, meanAbsoluteCents = _voiced == 0 ? (double?)null : _absoluteError / _voiced };

    public void Reset()
    {
        _analysis = new(); _history.Clear(); Voice = [];
        _frames = _voiced = _accurate = 0; _absoluteError = 0; _minMidi = _maxMidi = null;
        Current = new(0, null, null, null, null, null, null, "Ожидание голоса");
        Notify();
    }

    public FreePitchFrame Observe(PitchMeasurement measurement, long milliseconds)
    {
        Current = _analysis.Analyze(measurement, Math.Max(0, milliseconds)); _frames++;
        if (Current.Midi is { } midi)
        {
            _voiced++; _absoluteError += Math.Abs(Current.DeviationCents!.Value);
            if (Math.Abs(Current.DeviationCents.Value) <= 25) _accurate++;
            _minMidi = Math.Min(_minMidi ?? midi, midi); _maxMidi = Math.Max(_maxMidi ?? midi, midi);
        }
        _history.Enqueue(new(Current.Milliseconds / 1000.0, Current.MidiPitch, Current.DeviationCents));
        while (_history.TryPeek(out var oldest) && oldest.Seconds < Cursor - 30) _history.Dequeue();
        Voice = _history.ToArray(); Notify(); return Current;
    }

    private void Notify()
    {
        foreach (string property in new[] { nameof(Note), nameof(Octave), nameof(Frequency), nameof(Movement), nameof(Intonation),
                     nameof(Duration), nameof(ViewStart), nameof(Cursor), nameof(Summary), nameof(Statistics) }) OnChanged(property);
    }
}
