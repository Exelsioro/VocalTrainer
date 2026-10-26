using VocalTrainer.Infrastructure;

namespace VocalTrainer.ViewModels;

public sealed class TimelineStep(string note, string duration, int index) : ObservableObject
{
    private bool _active;
    private bool _completed;
    public string Note { get; } = note;
    public string Duration { get; } = duration;
    public int Index { get; } = index;
    public bool Active { get => _active; set => Set(ref _active, value); }
    public bool Completed { get => _completed; set => Set(ref _completed, value); }
}
