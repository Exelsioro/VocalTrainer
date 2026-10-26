using System.IO;
using NAudio.Wave;

namespace VocalTrainer.Services;

public sealed record MicrophoneOption(int Id, string Name)
{
    public override string ToString() => Name;
}

/// <summary>Owns mic buffers, tone playback and *opt-in* WAV files. No audio is stored during monitoring.</summary>
public interface IAudioService : IDisposable
{
    event EventHandler<Exception?>? MonitoringStopped;
    bool IsMonitoring { get; }
    bool IsRecording { get; }
    void StartMonitoring(int deviceId);
    void StopMonitoring();
    PitchMeasurement ReadPitch(double noiseGateRms = PitchDetector.DefaultGateRms);
    void PlayTone(double frequency, double seconds);
    void StopTone();
    string StartRecording(string directory);
    string? FinishRecording();
    string? TakeAutomaticallyFinishedRecording();
}

public sealed class AudioService : IAudioService
{
    private readonly object _bufferLock = new();
    private readonly object _recordLock = new();
    private readonly float[] _ring = new float[PitchDetector.FrameSamples];
    private int _ringIndex;
    private int _samplesReceived;
    private long _totalSamples, _lastReadSampleCount;
    private WaveInEvent? _mic;
    private WaveOutEvent? _player;
    private WaveFileWriter? _writer;
    private string? _recordingPath;
    private string? _automaticallyFinishedRecording;
    private bool _disposed;

    public event EventHandler<Exception?>? MonitoringStopped;
    public bool IsMonitoring => _mic is not null;
    public bool IsRecording { get { lock (_recordLock) return _writer is not null; } }

    public static IReadOnlyList<MicrophoneOption> FindMicrophones()
    {
        var found = new List<MicrophoneOption>();
        for (int index = 0; index < WaveIn.DeviceCount; index++)
            found.Add(new MicrophoneOption(index, WaveIn.GetCapabilities(index).ProductName));
        return found;
    }

    public void StartMonitoring(int deviceId)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_mic is not null) throw new InvalidOperationException("Микрофон уже включён.");
        lock (_bufferLock) { Array.Clear(_ring); _ringIndex = 0; _samplesReceived = 0; _totalSamples = 0; _lastReadSampleCount = 0; }
        _automaticallyFinishedRecording = null;
        var mic = new WaveInEvent
        {
            DeviceNumber = deviceId,
            WaveFormat = new WaveFormat(PitchDetector.SampleRate, 16, 1),
            BufferMilliseconds = 75,
            NumberOfBuffers = 3
        };
        mic.DataAvailable += OnDataAvailable;
        mic.RecordingStopped += OnRecordingStopped;
        _mic = mic;
        try { mic.StartRecording(); }
        catch
        {
            _mic = null;
            mic.DataAvailable -= OnDataAvailable;
            mic.RecordingStopped -= OnRecordingStopped;
            mic.Dispose();
            throw;
        }
    }

    public PitchMeasurement ReadPitch(double noiseGateRms = PitchDetector.DefaultGateRms)
    {
        var samples = new float[PitchDetector.FrameSamples];
        lock (_bufferLock)
        {
            if (_samplesReceived < samples.Length || _totalSamples == _lastReadSampleCount)
                return new(null, 0, 0);
            _lastReadSampleCount = _totalSamples;
            int count = samples.Length - _ringIndex;
            Array.Copy(_ring, _ringIndex, samples, 0, count);
            Array.Copy(_ring, 0, samples, count, _ringIndex);
        }
        return PitchDetector.Detect(samples, gateRms: noiseGateRms);
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        lock (_bufferLock)
        {
            _totalSamples += e.BytesRecorded / 2;
            for (int i = 0; i + 1 < e.BytesRecorded; i += 2)
            {
                short value = unchecked((short)(e.Buffer[i] | (e.Buffer[i + 1] << 8)));
                _ring[_ringIndex] = value / 32768f;
                _ringIndex = (_ringIndex + 1) % _ring.Length;
                _samplesReceived = Math.Min(_ring.Length, _samplesReceived + 1);
            }
        }
        lock (_recordLock) _writer?.Write(e.Buffer, 0, e.BytesRecorded);
    }

    public void StopMonitoring()
    {
        if (_mic is { } mic) mic.StopRecording();
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs e)
    {
        if (sender is not WaveInEvent mic) return;
        Exception? error = e.Exception;
        try { _automaticallyFinishedRecording = FinishRecording(); }
        catch (Exception ex) { error ??= ex; }
        mic.DataAvailable -= OnDataAvailable;
        mic.RecordingStopped -= OnRecordingStopped;
        if (ReferenceEquals(_mic, mic)) _mic = null;
        mic.Dispose();
        MonitoringStopped?.Invoke(this, error);
    }

    public void PlayTone(double frequency, double seconds)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        StopTone();
        var player = new WaveOutEvent { DesiredLatency = 130 };
        try
        {
            player.Init(new ToneProvider(frequency, seconds));
            player.Play();
            _player = player;
        }
        catch { player.Dispose(); throw; }
    }

    public void StopTone()
    {
        var player = _player;
        _player = null;
        if (player is null) return;
        try { player.Stop(); } catch { }
        player.Dispose();
    }

    public string StartRecording(string directory)
    {
        if (_mic is null) throw new InvalidOperationException("Сначала включи микрофон.");
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, $"voice-{DateTime.Now:yyyyMMdd-HHmmss-fff}.wav");
        lock (_recordLock)
        {
            if (_writer is not null) throw new InvalidOperationException("Запись уже идёт.");
            _writer = new WaveFileWriter(path, _mic.WaveFormat);
            _recordingPath = path;
        }
        return path;
    }

    public string? FinishRecording()
    {
        lock (_recordLock)
        {
            _writer?.Dispose();
            _writer = null;
            string? path = _recordingPath;
            _recordingPath = null;
            return path;
        }
    }

    public string? TakeAutomaticallyFinishedRecording()
    {
        string? path = _automaticallyFinishedRecording;
        _automaticallyFinishedRecording = null;
        return path;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        StopTone();
        var mic = _mic;
        _mic = null;
        if (mic is not null)
        {
            mic.DataAvailable -= OnDataAvailable;
            mic.RecordingStopped -= OnRecordingStopped;
            try { mic.StopRecording(); } catch { }
            mic.Dispose();
        }
        FinishRecording();
    }
}
