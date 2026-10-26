using System.IO;
using System.Collections.ObjectModel;
using System.Text.Json;
using VocalTrainer.Infrastructure;
using VocalTrainer.Practice;
using VocalTrainer.Services;

namespace VocalTrainer.ViewModels;

public sealed class ExerciseEditorViewModel : ObservableObject
{
    private readonly IDialogService _dialogs;
    private readonly Action<ExerciseDocument, string> _activate;
    private readonly Func<bool> _available;
    private readonly Action<string, object> _log;
    private string _title = "Моё упражнение";
    private string _description = "Тренировка контроля высоты";
    private string _goalsText = "Точное попадание";
    private string _tasksText = "Удерживать ноту без напряжения";
    private EditableSection? _selectedSection;
    private EditableNote? _selectedStep;
    private string _status = "Создай упражнение или открой готовый JSON.";

    public ObservableCollection<EditableSection> Sections { get; } = [];
    public string Title { get => _title; set => Set(ref _title, value); }
    public string Description { get => _description; set => Set(ref _description, value); }
    public string GoalsText { get => _goalsText; set => Set(ref _goalsText, value); }
    public string TasksText { get => _tasksText; set => Set(ref _tasksText, value); }
    public EditableSection? SelectedSection
    {
        get => _selectedSection;
        set { if (Set(ref _selectedSection, value)) { SelectedStep = value?.Notes.FirstOrDefault(); Refresh(); } }
    }
    public EditableNote? SelectedStep
    {
        get => _selectedStep;
        set { if (Set(ref _selectedStep, value)) Refresh(); }
    }
    public string Status { get => _status; private set => Set(ref _status, value); }
    public bool CanEdit => _available();

    public RelayCommand NewCommand { get; }
    public RelayCommand ImportCommand { get; }
    public RelayCommand SaveCommand { get; }
    public RelayCommand AddSectionCommand { get; }
    public RelayCommand RemoveSectionCommand { get; }
    public RelayCommand SectionUpCommand { get; }
    public RelayCommand SectionDownCommand { get; }
    public RelayCommand AddNoteCommand { get; }
    public RelayCommand RemoveNoteCommand { get; }
    public RelayCommand NoteUpCommand { get; }
    public RelayCommand NoteDownCommand { get; }

    public ExerciseEditorViewModel(IDialogService dialogs, Action<ExerciseDocument, string> activate,
        Func<bool> available, Action<string, object> log)
    {
        _dialogs = dialogs; _activate = activate; _available = available; _log = log;
        NewCommand = new RelayCommand(New, () => CanEdit);
        ImportCommand = new RelayCommand(Import, () => CanEdit);
        SaveCommand = new RelayCommand(Save, () => CanEdit);
        AddSectionCommand = new RelayCommand(AddSection, () => CanEdit);
        RemoveSectionCommand = new RelayCommand(RemoveSection, () => CanEdit && SelectedSection is not null);
        SectionUpCommand = new RelayCommand(() => MoveSection(-1), () => CanEdit && Sections.IndexOf(SelectedSection!) > 0);
        SectionDownCommand = new RelayCommand(() => MoveSection(1), () => CanEdit && SelectedSection is not null && Sections.IndexOf(SelectedSection) < Sections.Count - 1);
        AddNoteCommand = new RelayCommand(AddNote, () => CanEdit && SelectedSection is not null);
        RemoveNoteCommand = new RelayCommand(RemoveNote, () => CanEdit && SelectedSection is not null && SelectedStep is not null);
        NoteUpCommand = new RelayCommand(() => MoveNote(-1), () => CanEdit && SelectedSection is not null && SelectedStep is not null && SelectedSection.Notes.IndexOf(SelectedStep) > 0);
        NoteDownCommand = new RelayCommand(() => MoveNote(1), () => CanEdit && SelectedSection is not null && SelectedStep is not null && SelectedSection.Notes.IndexOf(SelectedStep) < SelectedSection.Notes.Count - 1);
        New();
    }

    public void Refresh()
    {
        OnChanged(nameof(CanEdit));
        NewCommand.Refresh(); ImportCommand.Refresh(); SaveCommand.Refresh();
        AddSectionCommand.Refresh(); RemoveSectionCommand.Refresh();
        SectionUpCommand.Refresh(); SectionDownCommand.Refresh();
        AddNoteCommand.Refresh(); RemoveNoteCommand.Refresh(); NoteUpCommand.Refresh(); NoteDownCommand.Refresh();
    }

    private void New()
    {
        if (!CanEdit) return;
        Title = "Моё упражнение"; Description = "Тренировка контроля высоты";
        GoalsText = "Точное попадание"; TasksText = "Удерживать ноту без напряжения";
        Sections.Clear();
        var section = new EditableSection { Title = "Разминка" };
        section.Notes.Add(new EditableNote { Note = "C3" });
        Sections.Add(section); SelectedSection = section;
        Status = "Новый документ. Для сохранения нажми «Сохранить JSON».";
        Refresh();
    }

    public void LoadFrom(ExerciseDocument document)
    {
        if (!CanEdit) return;
        Title = document.Title; Description = document.Description;
        GoalsText = string.Join(Environment.NewLine, document.Goals ?? []);
        TasksText = string.Join(Environment.NewLine, document.Tasks ?? []);
        Sections.Clear();
        foreach (var section in ExerciseEditorDocument.Unpack(document)) Sections.Add(section);
        SelectedSection = Sections.FirstOrDefault();
        Status = "Загружено в редактор. Исходный JSON не будет изменён без сохранения.";
        Refresh();
    }

    private void Import()
    {
        if (!CanEdit) return;
        string? path = _dialogs.SelectExercise();
        if (path is null) return;
        try { LoadFrom(ExerciseIO.Load(path)); Status = "Редактируется: " + Path.GetFileName(path); }
        catch (Exception ex) { _dialogs.Error("Ошибка импорта", ex.Message); Status = ex.Message; }
    }

    private void Save()
    {
        if (!CanEdit) return;
        try
        {
            var doc = ExerciseEditorDocument.Build(Title, Description, GoalsText, TasksText, Sections);
            string? file = _dialogs.SaveExercise();
            if (file is null) return;
            // Write atomically, so a failure cannot corrupt an existing exercise.
            string temp = file + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(temp, JsonSerializer.Serialize(doc, ExerciseIO.Options));
                File.Move(temp, file, true);
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
            _activate(doc, file);
            _log("exercise_editor_saved", new { title = doc.Title, sections = doc.Sections.Count, steps = doc.Sections.Sum(s => s.Notes.Count) });
            Status = $"Сохранено и загружено: {Path.GetFileName(file)}";
        }
        catch (Exception ex) { Status = "Ошибка: " + ex.Message; _dialogs.Error("Проверка / сохранение JSON", ex.Message); }
    }

    private void AddSection()
    {
        var section = new EditableSection { Title = $"Секция {Sections.Count + 1}" };
        section.Notes.Add(new EditableNote());
        Sections.Add(section); SelectedSection = section; Refresh();
    }
    private void RemoveSection()
    {
        if (SelectedSection is null) return;
        int i = Sections.IndexOf(SelectedSection);
        Sections.RemoveAt(i);
        SelectedSection = Sections.ElementAtOrDefault(Math.Min(i, Sections.Count - 1)); Refresh();
    }
    private void MoveSection(int delta)
    {
        if (SelectedSection is null) return;
        int i = Sections.IndexOf(SelectedSection), next = i + delta;
        if (next < 0 || next >= Sections.Count) return;
        Sections.Move(i, next); Refresh();
    }
    private void AddNote()
    {
        if (SelectedSection is null) return;
        var note = new EditableNote { Note = SelectedStep?.Note ?? "C3" };
        SelectedSection.Notes.Add(note); SelectedStep = note;
    }
    private void RemoveNote()
    {
        if (SelectedSection is null || SelectedStep is null) return;
        int i = SelectedSection.Notes.IndexOf(SelectedStep);
        SelectedSection.Notes.RemoveAt(i);
        SelectedStep = SelectedSection.Notes.ElementAtOrDefault(Math.Min(i, SelectedSection.Notes.Count - 1));
    }
    private void MoveNote(int delta)
    {
        if (SelectedSection is null || SelectedStep is null) return;
        int i = SelectedSection.Notes.IndexOf(SelectedStep), next = i + delta;
        if (next < 0 || next >= SelectedSection.Notes.Count) return;
        SelectedSection.Notes.Move(i, next); Refresh();
    }
}
