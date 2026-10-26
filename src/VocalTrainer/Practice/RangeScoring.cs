namespace VocalTrainer.Practice;

public sealed record RangeSample(double? FrequencyHz, double Confidence);

public sealed record RangeMeasurementResult(int Midi, int TotalFrames, int VoicedFrames,
    double VoiceCoveragePercent, double? MedianDeviationCents, double? MedianAbsoluteDeviationCents,
    bool Accurate, string Conclusion)
{
    public static RangeMeasurementResult Analyze(int midi, IReadOnlyList<RangeSample> samples,
        double toleranceCents = 50, double minimumConfidence = 0.65)
    {
        if (midi is < 0 or > 127) throw new ArgumentOutOfRangeException(nameof(midi));
        ArgumentNullException.ThrowIfNull(samples);
        if (toleranceCents <= 0 || !double.IsFinite(toleranceCents)) throw new ArgumentOutOfRangeException(nameof(toleranceCents));
        double targetHz = NoteCatalog.ToFrequency(midi);
        var cents = samples.Where(s => s.FrequencyHz is > 0 && s.Confidence >= minimumConfidence)
            .Select(s => NoteCatalog.Cents(s.FrequencyHz!.Value, targetHz))
            .Where(double.IsFinite).Order().ToArray();
        double coverage = samples.Count == 0 ? 0 : 100.0 * cents.Length / samples.Count;
        if (cents.Length < 6 || coverage < 55)
            return new(midi, samples.Count, cents.Length, coverage, null, null, false,
                "Недостаточно надёжных измерений. Повтори при спокойном звучании.");
        double median = Median(cents);
        double mad = Median(cents.Select(c => Math.Abs(c - median)).Order().ToArray());
        bool accurate = Math.Abs(median) <= toleranceCents && mad <= 35;
        string conclusion = accurate ? "Высота распознана достаточно точно; комфорт оцени самостоятельно."
            : "Высота нестабильна или не совпадает с эталоном; это не оценка здоровья голоса.";
        return new(midi, samples.Count, cents.Length, coverage, median, mad, accurate, conclusion);
    }

    private static double Median(double[] ordered) => ordered.Length % 2 == 1
        ? ordered[ordered.Length / 2]
        : (ordered[ordered.Length / 2 - 1] + ordered[ordered.Length / 2]) / 2;
}
