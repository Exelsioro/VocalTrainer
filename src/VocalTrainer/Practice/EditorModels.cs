using System.Collections.ObjectModel;
using VocalTrainer.Infrastructure;

namespace VocalTrainer.Practice;

public sealed class EditableSection : ObservableObject
{
    private string _title = "Новая секция";
    private string _description = "";
    public string Title { get => _title; set => Set(ref _title, value); }
    public string Description { get => _description; set => Set(ref _description, value); }
    public ObservableCollection<EditableNote> Notes { get; } = [];
    public override string ToString() => Title;
}

public sealed class EditableNote : ObservableObject
{
    private string _note = "C3";
    private double _previewSeconds = 2;
    private double _holdSeconds = 3;
    private double _transitionSeconds = 1;
    private double? _toleranceCents;
    private double? _requiredAccuracy;
    private int? _maxAttempts;
    private bool? _autoRepeatOnFail;

    public string Note { get => _note; set => Set(ref _note, value); }
    public double PreviewSeconds { get => _previewSeconds; set => Set(ref _previewSeconds, value); }
    public double HoldSeconds { get => _holdSeconds; set => Set(ref _holdSeconds, value); }
    public double TransitionSeconds { get => _transitionSeconds; set => Set(ref _transitionSeconds, value); }
    public double? ToleranceCents { get => _toleranceCents; set => Set(ref _toleranceCents, value); }
    public double? RequiredAccuracy { get => _requiredAccuracy; set => Set(ref _requiredAccuracy, value); }
    public int? MaxAttempts { get => _maxAttempts; set => Set(ref _maxAttempts, value); }
    public bool? AutoRepeatOnFail { get => _autoRepeatOnFail; set => Set(ref _autoRepeatOnFail, value); }
}

/// <summary>Pure conversion layer: editing does not mutate a loaded or running exercise.</summary>
public static class ExerciseEditorDocument
{
    public static ExerciseDocument Build(string title, string description, string goalsText, string tasksText,
        IEnumerable<EditableSection> sections)
    {
        var doc = new ExerciseDocument
        {
            Title = title.Trim(), Description = description.Trim(),
            Goals = SplitLines(goalsText), Tasks = SplitLines(tasksText),
            Sections = sections.Select(s => new ExerciseSection
            {
                Title = s.Title.Trim(), Description = s.Description.Trim(),
                Notes = s.Notes.Select(n => new ExerciseNote
                {
                    Note = n.Note.Trim(), PreviewSeconds = n.PreviewSeconds,
                    HoldSeconds = n.HoldSeconds, TransitionSeconds = n.TransitionSeconds,
                    ToleranceCents = n.ToleranceCents, RequiredAccuracy = n.RequiredAccuracy,
                    AutoRepeatOnFail = n.AutoRepeatOnFail, MaxAttempts = n.MaxAttempts
                }).ToList()
            }).ToList()
        };
        ExerciseIO.Validate(doc);
        return doc;
    }

    public static List<EditableSection> Unpack(ExerciseDocument doc)
    {
        ExerciseIO.Validate(doc);
        return doc.Sections.Select(s =>
        {
            var section = new EditableSection { Title = s.Title, Description = s.Description ?? "" };
            foreach (var n in s.Notes)
                section.Notes.Add(new EditableNote
                {
                    Note = n.Note, PreviewSeconds = n.PreviewSeconds, HoldSeconds = n.HoldSeconds,
                    TransitionSeconds = n.TransitionSeconds, ToleranceCents = n.ToleranceCents,
                    RequiredAccuracy = n.RequiredAccuracy, MaxAttempts = n.MaxAttempts,
                    AutoRepeatOnFail = n.AutoRepeatOnFail
                });
            return section;
        }).ToList();
    }

    private static List<string>? SplitLines(string text)
    {
        var lines = text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(x => !string.IsNullOrWhiteSpace(x)).ToList();
        return lines.Count == 0 ? null : lines;
    }
}
