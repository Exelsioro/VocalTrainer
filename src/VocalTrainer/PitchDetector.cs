namespace VocalTrainer;

public readonly record struct PitchMeasurement(double? FrequencyHz, double Rms, double Confidence)
{
    public bool IsVoiced => FrequencyHz.HasValue;
}

/// <summary>
/// YIN-inspired normalized difference function with parabolic interpolation.
/// Single-channel, mono, 44.1kHz, ~93 ms window. It estimates fundamental
/// frequency rather than choosing the loudest FFT peak.
/// </summary>
public static class PitchDetector
{
    public const int FrameSamples = 4096;
    public const int SampleRate = 44100;
    public const double MinHz = 65;
    public const double MaxHz = 1100;
    public const double DefaultGateRms = 0.008;
    private const double Threshold = 0.16;

    public static PitchMeasurement Detect(ReadOnlySpan<float> samples, int sampleRate = SampleRate, double gateRms = DefaultGateRms)
    {
        if (samples.Length < FrameSamples || sampleRate <= 0)
            return new(null, 0, 0);

        ReadOnlySpan<float> frame = samples[^FrameSamples..];
        double squared = 0;
        foreach (float s in frame) squared += s * s;
        double rms = Math.Sqrt(squared / frame.Length);
        if (rms < Math.Max(0, gateRms)) return new(null, rms, 0);

        // Use the first half against shifted copies of itself.
        int compareLength = FrameSamples / 2;
        int minTau = Math.Max(2, (int)Math.Floor(sampleRate / MaxHz));
        int maxTau = Math.Min(compareLength - 2, (int)Math.Ceiling(sampleRate / MinHz));
        var difference = new double[maxTau + 1];
        var cmnd = new double[maxTau + 1];
        cmnd[0] = 1;
        double cumulative = 0;

        for (int tau = 1; tau <= maxTau; tau++)
        {
            double sum = 0;
            for (int i = 0; i < compareLength; i++)
            {
                double d = frame[i] - frame[i + tau];
                sum += d * d;
            }
            difference[tau] = sum;
            cumulative += sum;
            cmnd[tau] = cumulative > 1e-20 ? sum * tau / cumulative : 1;
        }

        int chosen = -1;
        for (int tau = minTau; tau <= maxTau; tau++)
        {
            if (cmnd[tau] < Threshold)
            {
                while (tau + 1 <= maxTau && cmnd[tau + 1] < cmnd[tau]) tau++;
                chosen = tau;
                break;
            }
        }

        if (chosen < 0)
        {
            // A little tolerance for breathy / imperfect voices.
            int best = minTau;
            for (int tau = minTau + 1; tau <= maxTau; tau++)
                if (cmnd[tau] < cmnd[best]) best = tau;
            if (cmnd[best] > 0.23) return new(null, rms, 1 - cmnd[best]);
            chosen = best;
        }

        double refined = chosen;
        if (chosen > 1 && chosen < maxTau)
        {
            double l = cmnd[chosen - 1], c = cmnd[chosen], r = cmnd[chosen + 1];
            double denominator = l - 2 * c + r;
            if (Math.Abs(denominator) > 1e-12)
                refined += Math.Clamp(0.5 * (l - r) / denominator, -1.0, 1.0);
        }

        double hz = sampleRate / refined;
        double confidence = Math.Clamp(1.0 - cmnd[chosen], 0, 1);
        if (!double.IsFinite(hz) || hz < MinHz || hz > MaxHz)
            return new(null, rms, confidence);
        return new(hz, rms, confidence);
    }
}
