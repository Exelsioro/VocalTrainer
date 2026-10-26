using System.Buffers.Binary;
using System.IO;
using System.Text;

namespace VocalTrainer.Melody;

/// <summary>Pure Standard MIDI File reader (format 0/1, ticks-per-quarter timing). No audio is loaded.</summary>
public static class MidiSongReader
{
    private sealed record RawNote(long StartTick, long EndTick, int Midi, int Channel);
    private sealed record Tempo(long Tick, int MicrosecondsPerQuarter);
    private sealed class TrackData
    {
        public string Name = "";
        public List<RawNote> Notes = [];
        public List<Tempo> Tempos = [];
        public List<(long Tick, string Text)> Lyrics = [];
        public List<(long Tick, string Text)> KaraokeText = [];
    }

    public static MidiSong Read(string filePath)
    {
        var bytes = File.ReadAllBytes(filePath);
        return ReadBytes(bytes, Path.GetFileName(filePath));
    }

    public static MidiSong ReadBytes(byte[] bytes, string name = "import.mid")
    {
        if (bytes.Length > 10 * 1024 * 1024) throw new FormatException("MIDI-файл превышает 10 МБ.");
        var r = new Reader(bytes);
        if (r.Ascii(4) != "MThd") throw new FormatException("Отсутствует MIDI-заголовок MThd.");
        int headerLength = checked((int)r.U32());
        if (headerLength < 6 || headerLength > 1024) throw new FormatException("Некорректный MIDI-заголовок.");
        int format = r.U16();
        int trackCount = r.U16();
        int division = r.U16();
        r.Skip(headerLength - 6);
        if (format is not (0 or 1)) throw new FormatException("Поддерживаются только MIDI форматов 0 и 1.");
        if (trackCount is < 1 or > 128 || (format == 0 && trackCount != 1))
            throw new FormatException("Недопустимое количество MIDI-треков.");
        if (division == 0 || (division & 0x8000) != 0)
            throw new FormatException("SMPTE-тайминг не поддерживается; экспортируй MIDI в PPQ.");
        var tracks = new List<TrackData>();
        for (int i = 0; i < trackCount; i++)
        {
            if (r.Ascii(4) != "MTrk") throw new FormatException("Не найден блок MIDI-трека MTrk.");
            int length = checked((int)r.U32());
            if (length < 0 || length > r.Remaining) throw new FormatException("Повреждён блок MIDI-трека.");
            tracks.Add(ParseTrack(r.Bytes(length), i));
        }
        var tempos = tracks.SelectMany(t => t.Tempos).OrderBy(t => t.Tick).ToList();
        var tempoMap = new List<(long Tick, double Seconds, int Us)>() { (0, 0, 500000) };
        foreach (var tempo in tempos)
        {
            if (tempo.Tick < 0 || tempo.MicrosecondsPerQuarter < 1000) continue;
            var previous = tempoMap[^1];
            double seconds = previous.Seconds + (tempo.Tick - previous.Tick) * (previous.Us / (division * 1_000_000.0));
            if (tempo.Tick == previous.Tick) tempoMap[^1] = (tempo.Tick, seconds, tempo.MicrosecondsPerQuarter);
            else tempoMap.Add((tempo.Tick, seconds, tempo.MicrosecondsPerQuarter));
        }
        double ToSeconds(long tick)
        {
            int low = 0, high = tempoMap.Count - 1;
            while (low < high)
            {
                int mid = (low + high + 1) / 2;
                if (tempoMap[mid].Tick <= tick) low = mid;
                else high = mid - 1;
            }
            var entry = tempoMap[low];
            return entry.Seconds + (tick - entry.Tick) * entry.Us / (division * 1_000_000.0);
        }
        var resultTracks = new List<MidiTrack>();
        for (int i = 0; i < tracks.Count; i++)
        {
            var raw = tracks[i].Notes;
            if (raw.Count == 0) continue;
            if (raw.Count > 100000) throw new FormatException("Слишком много MIDI-нот.");
            var notes = raw.Select(n => new MidiNote(n.Midi, ToSeconds(n.StartTick),
                Math.Max(.001, ToSeconds(n.EndTick) - ToSeconds(n.StartTick)), n.Channel))
                .OrderBy(n => n.StartSeconds).ThenByDescending(n => n.Midi).ToArray();
            int overlap = 0;
            double lastEnd = -1;
            foreach (var note in notes)
            {
                if (note.StartSeconds < lastEnd - .002) overlap++;
                lastEnd = Math.Max(lastEnd, note.EndSeconds);
            }
            string title = string.IsNullOrWhiteSpace(tracks[i].Name) ? $"Трек {i + 1}" : tracks[i].Name;
            resultTracks.Add(new MidiTrack(i, title, notes, overlap));
        }
        if (resultTracks.Count == 0) throw new FormatException("В MIDI нет нотных событий.");
        var lyricTracks = tracks.Select((track, i) => new MidiLyricTrack(i,
                string.IsNullOrWhiteSpace(track.Name) ? $"Трек {i + 1}" : track.Name,
                LyricIO.FromMidi((track.Lyrics.Count > 0 ? track.Lyrics :
                    track.KaraokeText.Any(l => l.Text.StartsWith('/') || l.Text.StartsWith('\\') || l.Text.StartsWith("@KMIDI", StringComparison.OrdinalIgnoreCase))
                        ? track.KaraokeText : [])
                    .Select(l => (ToSeconds(l.Tick), l.Text)))))
            .Where(t => t.Lines.Any(l => l.Text.Length > 0)).ToArray();
        return new MidiSong(name, resultTracks, division) { LyricTracks = lyricTracks };
    }

    private static TrackData ParseTrack(byte[] bytes, int index)
    {
        var r = new Reader(bytes);
        var result = new TrackData();
        var open = new Dictionary<(int Channel, int Pitch), Stack<long>>();
        long tick = 0;
        int running = 0;
        int eventCount = 0;
        while (r.Remaining > 0)
        {
            if (++eventCount > 200000) throw new FormatException("Слишком много MIDI-событий.");
            tick = checked(tick + r.VarInt());
            int status = r.Peek();
            if (status >= 0x80)
            {
                status = r.U8();
                if (status < 0xF0) running = status;
                else running = 0;
            }
            else
            {
                status = running;
                if (status == 0) throw new FormatException("Неверный MIDI running status.");
            }
            if (status == 0xFF)
            {
                int metaType = r.U8();
                int size = checked((int)r.VarInt());
                byte[] payload = r.Bytes(size);
                if (metaType == 0x03 && result.Name.Length == 0)
                    result.Name = LyricIO.DecodeText(payload).Trim('\0', ' ');
                if (metaType == 0x05) result.Lyrics.Add((tick, LyricIO.DecodeText(payload)));
                if (metaType == 0x01) result.KaraokeText.Add((tick, LyricIO.DecodeText(payload)));
                if (metaType == 0x51 && size == 3)
                    result.Tempos.Add(new Tempo(tick, (payload[0] << 16) | (payload[1] << 8) | payload[2]));
                if (metaType == 0x2F) break;
                continue;
            }
            if (status is 0xF0 or 0xF7)
            {
                int size = checked((int)r.VarInt());
                r.Skip(size);
                continue;
            }
            if (status >= 0xF0) throw new FormatException("Неподдерживаемое MIDI-системное событие.");
            int channel = status & 0x0F;
            int type = status & 0xF0;
            int noteOrData = r.U8();
            int velocityOrData = type is 0xC0 or 0xD0 ? 0 : r.U8();
            if (type is not (0x80 or 0x90)) continue;
            int pitch = noteOrData;
            var key = (channel, pitch);
            if (type == 0x90 && velocityOrData > 0)
            {
                if (!open.TryGetValue(key, out var stack)) open[key] = stack = new Stack<long>();
                stack.Push(tick);
            }
            else if (open.TryGetValue(key, out var stack) && stack.Count > 0)
            {
                long start = stack.Pop();
                if (tick > start) result.Notes.Add(new RawNote(start, tick, pitch, channel));
            }
        }
        // Unclosed note-ons are skipped rather than guessing their duration.
        return result;
    }

    private sealed class Reader(byte[] bytes)
    {
        private int _offset;
        public int Remaining => bytes.Length - _offset;
        public int Peek() => Remaining > 0 ? bytes[_offset] : throw new FormatException("Неожиданный конец MIDI.");
        public int U8() { int b = Peek(); _offset++; return b; }
        public int U16() => (U8() << 8) | U8();
        public uint U32() => (uint)((U8() << 24) | (U8() << 16) | (U8() << 8) | U8());
        public string Ascii(int n) => Encoding.ASCII.GetString(Bytes(n));
        public byte[] Bytes(int n)
        {
            if (n < 0 || n > Remaining) throw new FormatException("MIDI-данные обрезаны.");
            var part = bytes.AsSpan(_offset, n).ToArray();
            _offset += n;
            return part;
        }
        public void Skip(int n) { if (n < 0 || n > Remaining) throw new FormatException("Некорректная длина блока MIDI."); _offset += n; }
        public long VarInt()
        {
            long v = 0;
            for (int i = 0; i < 4; i++)
            {
                int b = U8();
                v = (v << 7) | (uint)(b & 0x7F);
                if ((b & 0x80) == 0) return v;
            }
            throw new FormatException("Недопустимое MIDI variable-length число.");
        }
    }
}

public sealed record MidiNote(int Midi, double StartSeconds, double DurationSeconds, int Channel)
{
    public double EndSeconds => StartSeconds + DurationSeconds;
}
public sealed record MidiTrack(int Index, string Title, IReadOnlyList<MidiNote> Notes, int OverlapCount)
{
    public double EndSeconds => Notes.Count == 0 ? 0 : Notes.Max(n => n.EndSeconds);
    public string Display => $"{Title} · {Notes.Count} нот" + (OverlapCount > 0 ? " · полифония" : "");
}
public sealed record MidiSong(string FileName, IReadOnlyList<MidiTrack> Tracks, int TicksPerQuarter)
{
    public IReadOnlyList<MidiLyricTrack> LyricTracks { get; init; } = [];
}
