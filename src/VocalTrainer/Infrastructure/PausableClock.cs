using System.Diagnostics;

namespace VocalTrainer.Infrastructure;

/// <summary>Monotonic logical clock; paused wall time never advances the exercise.
/// Caller owns the cancellation token. Safe to use on the WPF dispatcher thread.</summary>
public sealed class PausableClock
{
    private readonly Stopwatch _watch = Stopwatch.StartNew();
    private double _offsetSeconds;
    public bool IsPaused { get; private set; }
    public double ElapsedSeconds => _offsetSeconds + _watch.Elapsed.TotalSeconds;
    public void Seek(double seconds)
    {
        if (!double.IsFinite(seconds) || seconds < 0) throw new ArgumentOutOfRangeException(nameof(seconds));
        bool running = _watch.IsRunning;
        _watch.Reset(); _offsetSeconds = seconds;
        if (running) _watch.Start();
    }
    public void Pause() { if (IsPaused) return; _watch.Stop(); IsPaused = true; }
    public void Resume() { if (!IsPaused) return; IsPaused = false; _watch.Start(); }
    public void Stop() => _watch.Stop();

    public async Task DelayAsync(double seconds, CancellationToken token)
    {
        double deadline = ElapsedSeconds + Math.Max(0, seconds);
        while (true)
        {
            token.ThrowIfCancellationRequested();
            if (!IsPaused && ElapsedSeconds >= deadline) return;
            await Task.Delay(TimeSpan.FromMilliseconds(35), token);
        }
    }
}
