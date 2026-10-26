using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;

namespace VocalTrainer.Practice;

public sealed record ExerciseLibraryEntry(ExerciseDocument Document, string SourcePath)
{
    public string Display => $"{Document.Title} · {Path.GetFileName(SourcePath)}";
}

public sealed class ExerciseLibrary(string indexPath)
{
    private readonly HashSet<string> _remembered = new(StringComparer.OrdinalIgnoreCase);
    public ObservableCollection<ExerciseLibraryEntry> Entries { get; } = [];
    public string? Warning { get; private set; }

    public ExerciseLibraryEntry Add(ExerciseDocument document, string path, bool remember)
    {
        ExerciseIO.Validate(document);
        string fullPath = Path.GetFullPath(path);
        var entry = new ExerciseLibraryEntry(document, fullPath);
        int index = Entries.ToList().FindIndex(e => e.SourcePath.Equals(fullPath, StringComparison.OrdinalIgnoreCase));
        if (index < 0) Entries.Add(entry); else Entries[index] = entry;
        if (remember) { _remembered.Add(fullPath); SaveIndex(); }
        return entry;
    }

    public void Load(params string[] folders)
    {
        var paths = new List<string>();
        foreach (string folder in folders)
        {
            try { if (Directory.Exists(folder)) paths.AddRange(Directory.EnumerateFiles(folder, "*.json").Order()); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Warning = ex.Message; }
        }
        try
        {
            if (File.Exists(indexPath))
                foreach (string? path in JsonSerializer.Deserialize<List<string>>(File.ReadAllText(indexPath)) ?? [])
                    if (!string.IsNullOrWhiteSpace(path)) { _remembered.Add(path); paths.Add(path); }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { Warning = "Не удалось прочитать список упражнений: " + ex.Message; }
        foreach (string path in paths.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try { if (File.Exists(path)) Add(ExerciseIO.Load(path), path, false); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or FormatException or ArgumentException)
            { Warning = $"Не загружено {Path.GetFileName(path)}: {ex.Message}"; }
        }
    }

    private void SaveIndex()
    {
        string temp = indexPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(indexPath))!);
            File.WriteAllText(temp, JsonSerializer.Serialize(_remembered.Order().ToArray()));
            File.Move(temp, indexPath, true);
            Warning = null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { Warning = "Упражнение доступно, но список не сохранён: " + ex.Message; }
        finally { try { if (File.Exists(temp)) File.Delete(temp); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    }
}
