using NAudio.Wave;

namespace VocalTrainer;

/// <summary>Finite monophonic sine tone with gentle fade in/out to avoid clicks.</summary>
public sealed class ToneProvider : ISampleProvider
{
    public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(44100, 1);
    private readonly double _hz;
    private readonly long _totalSamples;
    private long _position;

    public ToneProvider(double hz, double durationSeconds)
    {
        _hz = hz;
        _totalSamples = (long)Math.Round(durationSeconds * WaveFormat.SampleRate);
    }

    public int Read(float[] buffer, int offset, int count)
    {
        int n = (int)Math.Min(count, _totalSamples - _position);
        if (n <= 0) return 0;
        for (int i = 0; i < n; i++)
        {
            long sample = _position + i;
            double attack = Math.Min(1, sample / (0.020 * WaveFormat.SampleRate));
            double release = Math.Min(1, (_totalSamples - 1 - sample) / (0.035 * WaveFormat.SampleRate));
            double envelope = Math.Clamp(Math.Min(attack, release), 0, 1);
            buffer[offset + i] = (float)(0.18 * envelope * Math.Sin(2 * Math.PI * _hz * sample / WaveFormat.SampleRate));
        }
        _position += n;
        return n;
    }
}
