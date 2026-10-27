using System.IO;
using System.Diagnostics;
using System.Windows;
using Microsoft.Win32;

namespace VocalTrainer.Services;

public interface IDialogService
{
    string? SelectExercise();
    string? SelectMidi();
    string? SelectLyrics();
    string? SaveLyrics();
    string? SaveExercise();
    string? SaveSessionExport(string suggestedName);
    void Error(string title, string message);
    void Info(string title, string message);
    void OpenDataFolder(string path);
}

public sealed class DesktopDialogService : IDialogService
{
    public string? SaveSessionExport(string suggestedName)
    {
        string folder = Path.Combine(AppDataPaths.Root, "Exports");
        Directory.CreateDirectory(folder);
        var dialog = new SaveFileDialog { Title = "Экспорт занятия для анализа", Filter = "Отчёт Vocal Trainer (*.zip)|*.zip",
            InitialDirectory = folder, FileName = suggestedName, DefaultExt = ".zip", AddExtension = true, OverwritePrompt = true };
        return dialog.ShowDialog(Application.Current.MainWindow) == true ? dialog.FileName : null;
    }

    public string? SelectLyrics()
    {
        var dialog = new OpenFileDialog { Title = "Текст песни с разметкой или без неё", Filter = "Текст песни (*.lrc;*.txt)|*.lrc;*.txt", CheckFileExists = true };
        return dialog.ShowDialog(Application.Current.MainWindow) == true ? dialog.FileName : null;
    }

    public string? SaveLyrics()
    {
        var dialog = new SaveFileDialog { Title = "Сохранить разметку текста", Filter = "LRC (*.lrc)|*.lrc", FileName = "lyrics.lrc", DefaultExt = ".lrc", AddExtension = true, OverwritePrompt = true };
        return dialog.ShowDialog(Application.Current.MainWindow) == true ? dialog.FileName : null;
    }

    public string? SelectExercise()
    {
        string bundled = Path.Combine(AppContext.BaseDirectory, "exercises");
        var dialog = new OpenFileDialog
        {
            Title = "Импорт JSON-упражнения",
            Filter = "Упражнения (*.json)|*.json|Все файлы (*.*)|*.*",
            InitialDirectory = Directory.Exists(bundled) ? bundled : AppContext.BaseDirectory,
            CheckFileExists = true
        };
        return dialog.ShowDialog(Application.Current.MainWindow) == true ? dialog.FileName : null;
    }

    public string? SelectMidi()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Импорт MIDI-мелодии",
            Filter = "Standard MIDI (*.mid;*.midi)|*.mid;*.midi|Все файлы (*.*)|*.*",
            CheckFileExists = true
        };
        return dialog.ShowDialog(Application.Current.MainWindow) == true ? dialog.FileName : null;
    }

    public string? SaveExercise()
    {
        string folder = Path.Combine(AppDataPaths.Root, "Exercises");
        Directory.CreateDirectory(folder);
        var dialog = new SaveFileDialog
        {
            Title = "Сохранить упражнение JSON",
            Filter = "Упражнение JSON (*.json)|*.json",
            InitialDirectory = folder,
            FileName = "my-exercise.json",
            DefaultExt = ".json",
            AddExtension = true,
            OverwritePrompt = true
        };
        return dialog.ShowDialog(Application.Current.MainWindow) == true ? dialog.FileName : null;
    }

    public void Error(string title, string message)
        => MessageBox.Show(Application.Current.MainWindow, message, title, MessageBoxButton.OK, MessageBoxImage.Error);

    public void Info(string title, string message)
        => MessageBox.Show(Application.Current.MainWindow, message, title, MessageBoxButton.OK, MessageBoxImage.Information);

    public void OpenDataFolder(string path)
    {
        Directory.CreateDirectory(path);
        Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
    }
}
