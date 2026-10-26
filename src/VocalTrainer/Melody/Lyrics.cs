using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace VocalTrainer.Melody;

public sealed record LyricCue(double Seconds, string Text);
public sealed record MidiLyricTrack(int TrackIndex, string Title, IReadOnlyList<LyricCue> Lines)
{
    public string Display => $"{Title} · текст, {Lines.Count(l => l.Text.Length > 0)} строк";
}

public static class LyricIO
{
    private static readonly Regex Stamp = new(@"\G\[(\d{1,6}):([0-5]?\d(?:[.,]\d{1,3})?)\]", RegexOptions.Compiled);
    private static readonly Regex Metadata = new(@"^\[[a-z]+:.*\]$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static string DecodeText(byte[] bytes)
    {
        if (bytes.Length >= 2 && bytes[0] == 255 && bytes[1] == 254) return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
        if (bytes.Length >= 2 && bytes[0] == 254 && bytes[1] == 255) return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);
        try { return new UTF8Encoding(false, true).GetString(bytes).TrimStart('\uFEFF'); }
        catch (DecoderFallbackException)
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return Encoding.GetEncoding(1251).GetString(bytes);
        }
    }

    public static string ReadText(string path)
    {
        if (new FileInfo(path).Length > 1024 * 1024) throw new FormatException("Текст превышает 1 МБ.");
        var bytes = File.ReadAllBytes(path);
        if (bytes.Length > 1024 * 1024) throw new FormatException("Текст превышает 1 МБ.");
        return DecodeText(bytes);
    }

    public static double ParseTime(string value)
    {
        string[] pieces = value.Trim().Replace(',', '.').Split(':');
        double result;
        if (pieces.Length == 1 && double.TryParse(pieces[0], NumberStyles.Float, CultureInfo.InvariantCulture, out result)) { }
        else if (pieces.Length == 2 && int.TryParse(pieces[0], NumberStyles.None, CultureInfo.InvariantCulture, out int minutes) &&
                 double.TryParse(pieces[1], NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out double seconds) &&
                 minutes >= 0 && seconds is >= 0 and < 60) result = minutes * 60.0 + seconds;
        else throw new FormatException("Время строки: секунды (12.5) или минуты:секунды (01:12.500).");
        if (!double.IsFinite(result) || result < 0 || result > 360000) throw new FormatException("Недопустимое время строки.");
        return result;
    }

    public static IReadOnlyList<LyricCue> ParseLrc(string text)
    {
        if (text.Length > 1024 * 1024) throw new FormatException("Текст превышает 1 МБ.");
        var lines = new List<LyricCue>();
        int number = 0;
        foreach (var raw in text.Replace("\r", "").Split('\n'))
        {
            number++;
            string line = raw.Trim().TrimStart('\uFEFF');
            if (line.Length == 0) continue;
            if (Metadata.IsMatch(line))
            {
                if (line.StartsWith("[offset:", StringComparison.OrdinalIgnoreCase) &&
                    (!int.TryParse(line[8..^1].Trim(), out int offset) || offset != 0))
                    throw new FormatException("Для LRC с offset сначала перенеси смещение в таймкоды; поддерживается offset:0.");
                continue;
            }
            var stamps = new List<double>();
            int position = 0;
            while (true)
            {
                var match = Stamp.Match(line, position);
                if (!match.Success) break;
                stamps.Add(ParseTime(match.Groups[1].Value + ":" + match.Groups[2].Value));
                position += match.Length;
            }
            if (stamps.Count == 0) throw new FormatException($"Строка {number}: ожидается [мм:сс.ммм]текст.");
            string words = line[position..].Trim();
            foreach (double time in stamps) lines.Add(new(time, words));
            if (lines.Count > 10000) throw new FormatException("Слишком много строк текста.");
        }
        var sorted = lines.OrderBy(l => l.Seconds).Distinct().ToArray();
        Validate(sorted);
        return sorted;
    }

    public static void Validate(IReadOnlyList<LyricCue> lines)
    {
        if (lines.Count is 0 or > 10000) throw new FormatException("Нужно от 1 до 10000 строк текста.");
        double previous = -1;
        foreach (var line in lines)
        {
            if (!double.IsFinite(line.Seconds) || line.Seconds < 0 || line.Seconds > 360000 || line.Seconds <= previous)
                throw new FormatException("Время строк должно возрастать без одинаковых меток.");
            if (line.Text is null || line.Text.Length > 3000 || line.Text.Contains('\n') || line.Text.Contains('\r'))
                throw new FormatException("Одна строка текста — до 3000 символов без переноса.");
            previous = line.Seconds;
        }
    }

    public static int At(IReadOnlyList<LyricCue> lines, double sourceSeconds)
    {
        int low = 0, high = lines.Count - 1, found = -1;
        while (low <= high)
        {
            int middle = low + (high - low) / 2;
            if (lines[middle].Seconds <= sourceSeconds) { found = middle; low = middle + 1; }
            else high = middle - 1;
        }
        return found;
    }

    public static string ToLrc(IReadOnlyList<LyricCue> lines)
    {
        Validate(lines);
        return string.Join(Environment.NewLine, lines.Select(l =>
        {
            long ms = (long)Math.Round(l.Seconds * 1000);
            return $"[{ms / 60000:00}:{ms % 60000 / 1000:00}.{ms % 1000:000}]{l.Text}";
        })) + Environment.NewLine;
    }

    public static IReadOnlyList<LyricCue> FromMidi(IEnumerable<(double Seconds, string Text)> events)
    {
        var lines = new List<LyricCue>();
        var words = new StringBuilder();
        double? start = null;
        void Flush()
        {
            if (words.ToString().Trim() is { Length: > 0 } value && start is { } time) lines.Add(new(time, value));
            words.Clear(); start = null;
        }
        foreach (var item in events.OrderBy(e => e.Seconds))
        {
            if (item.Text.StartsWith('@')) continue; // Karaoke metadata such as @T and @L.
            foreach (char symbol in item.Text)
            {
                if (symbol is '\r' or '\n' or '/' or '\\')
                { Flush(); lines.Add(new(item.Seconds, "")); }
                else if (symbol != '\0') { start ??= item.Seconds; words.Append(symbol); }
            }
        }
        Flush();
        return lines.GroupBy(l => l.Seconds).Select(g => g.Last()).OrderBy(l => l.Seconds).ToArray();
    }
}
