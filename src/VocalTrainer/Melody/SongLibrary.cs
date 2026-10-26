using System.Collections.ObjectModel;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace VocalTrainer.Melody;

public sealed record SavedLyricRow(string Time, string Text);
public sealed record SavedLyrics(string SourceName, bool External, int? SourceTrack, string PlainText,
    bool Dirty, SavedLyricRow[] Rows, LyricCue[] Cues);
public sealed record SavedSong(string Id, string Title)
{
    public string Display => Title;
}
public sealed record SavedSongState(int SchemaVersion, string Title, int? TrackIndex = null,
    SavedLyrics? Lyrics = null, string? TextFile = null);

/// <summary>Owns local copies; source paths are never required after import.</summary>
public sealed class SongLibrary(string folder)
{
    public ObservableCollection<SavedSong> Entries { get; } = [];
    public string? LastSelectedId { get; private set; }
    public string? Warning { get; private set; }
    private static bool IsId(string id) => Regex.IsMatch(id, "^[a-f0-9]{64}$");
    private string DirectoryFor(SavedSong song) => IsId(song.Id) ? Path.Combine(folder, song.Id) : throw new FormatException("Некорректный код песни.");
    public string MidiPath(SavedSong song) => Path.Combine(DirectoryFor(song), "track.mid");

    public void Load()
    {
        Entries.Clear(); Warning = null;
        try
        {
            if (!Directory.Exists(folder)) return;
            foreach (string directory in Directory.EnumerateDirectories(folder).Order())
            {
                string id = Path.GetFileName(directory);
                if (!IsId(id)) continue;
                try
                {
                    var candidate = new SavedSong(id, "");
                    var state = Read(candidate);
                    if (!File.Exists(MidiPath(candidate))) throw new FileNotFoundException("Нет сохранённого MIDI.");
                    Entries.Add(candidate with { Title = state.Title });
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or FormatException)
                { Warning = "Часть библиотеки недоступна: " + ex.Message; }
            }
            string selection = Path.Combine(folder, "selection.json");
            LastSelectedId = File.Exists(selection) ? JsonSerializer.Deserialize<string>(File.ReadAllText(selection)) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        { Warning = "Не удалось прочитать библиотеку песен: " + ex.Message; }
    }

    public SavedSong Import(string source)
    {
        byte[] bytes = File.ReadAllBytes(source);
        _ = MidiSongReader.ReadBytes(bytes); // Validate before making a library entry.
        string id = Convert.ToHexStringLower(SHA256.HashData(bytes));
        var existing = Entries.FirstOrDefault(s => s.Id == id);
        if (existing is not null) return existing;
        string title = Path.GetFileName(source);
        for (int suffix = 2; Entries.Any(s => s.Title.Equals(title, StringComparison.OrdinalIgnoreCase)); suffix++)
            title = $"{Path.GetFileName(source)} ({suffix})";
        var song = new SavedSong(id, title);
        Directory.CreateDirectory(DirectoryFor(song));
        string statePath = Path.Combine(DirectoryFor(song), "song.json");
        // Preserve saved lyrics if a previously unavailable entry is reimported.
        if (File.Exists(statePath)) song = song with { Title = Read(song).Title };
        WriteAtomic(MidiPath(song), bytes);
        if (!File.Exists(statePath)) WriteJson(statePath, new SavedSongState(1, song.Title));
        Entries.Add(song);
        return song;
    }

    public SavedSongState Read(SavedSong song)
    {
        var state = JsonSerializer.Deserialize<SavedSongState>(File.ReadAllText(Path.Combine(DirectoryFor(song), "song.json")));
        if (state is null || state.SchemaVersion != 1 || string.IsNullOrWhiteSpace(state.Title))
            throw new FormatException("Неподдерживаемые данные песни.");
        if (state.Lyrics is { } lyrics)
        {
            if (lyrics.Rows is null || lyrics.Cues is null || lyrics.Rows.Length > 10000 || lyrics.PlainText is null || lyrics.PlainText.Length > 1024 * 1024 ||
                lyrics.Rows.Any(r => r is null || r.Text is null || r.Text.Length > 3000 || r.Time is null || r.Time.Length > 100))
                throw new FormatException("Повреждён сохранённый текст.");
            if (lyrics.Cues.Length > 0) LyricIO.Validate(lyrics.Cues);
        }
        return state;
    }

    public void Save(SavedSong song, int? track, SavedLyrics lyrics, string? importedText = null)
    {
        var previous = Read(song);
        string? textFile = lyrics.Rows.Length == 0 && lyrics.PlainText.Length == 0 ? null : previous.TextFile;
        if (importedText is not null)
        {
            byte[] bytes = File.ReadAllBytes(importedText);
            if (bytes.Length > 4 * 1024 * 1024) throw new FormatException("Файл текста слишком большой.");
            string extension = Path.GetExtension(importedText).Equals(".lrc", StringComparison.OrdinalIgnoreCase) ? ".lrc" : ".txt";
            textFile = "lyrics-" + Convert.ToHexStringLower(SHA256.HashData(bytes)) + extension;
            WriteAtomic(Path.Combine(DirectoryFor(song), textFile), bytes);
        }
        WriteJson(Path.Combine(DirectoryFor(song), "song.json"), previous with { TrackIndex = track, Lyrics = lyrics, TextFile = textFile });
    }

    public void Select(SavedSong song)
    {
        WriteJson(Path.Combine(folder, "selection.json"), song.Id);
        LastSelectedId = song.Id;
    }

    private static void WriteJson<T>(string path, T value) => WriteAtomic(path, JsonSerializer.SerializeToUtf8Bytes(value));
    private static void WriteAtomic(string path, byte[] bytes)
    {
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllBytes(temporary, bytes); File.Move(temporary, path, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
