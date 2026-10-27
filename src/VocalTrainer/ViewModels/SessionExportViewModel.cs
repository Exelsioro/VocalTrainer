using System.Collections.ObjectModel;
using System.IO;
using VocalTrainer.Export;
using VocalTrainer.Infrastructure;

namespace VocalTrainer.ViewModels;

public sealed class SessionExportViewModel : ObservableObject, IDisposable
{
    private readonly string _root;
    private readonly Func<bool> _available;
    private readonly Func<string, string?> _save;
    private bool _busy, _disposed, _includeNotes = true, _includeDeviceInfo = true, _includeHistory = true;
    private string _status = "Выбери сохранённое занятие. Аудио не включается.", _preview = "";
    private ReportSource? _selected;
    private int _selectionVersion;
    public ObservableCollection<ReportSource> Sources { get; } = [];
    public ReportSource? SelectedSource
    {
        get => _selected;
        set { if (Set(ref _selected, value)) { RefreshAvailability(); _ = PreviewAsync(); } }
    }
    public bool IncludeNotes { get => _includeNotes; set => Set(ref _includeNotes, value); }
    public bool IncludeDeviceInfo { get => _includeDeviceInfo; set => Set(ref _includeDeviceInfo, value); }
    public bool IncludeHistory { get => _includeHistory; set => Set(ref _includeHistory, value); }
    public bool IsBusy { get => _busy; private set { if (Set(ref _busy, value)) RefreshAvailability(); } }
    public bool CanSelect => !IsBusy && !_disposed;
    public bool CanExport => CanSelect && SelectedSource is not null && _available();
    public string Status { get => _status; private set => Set(ref _status, value); }
    public string Preview { get => _preview; private set => Set(ref _preview, value); }
    public RelayCommand RefreshCommand { get; }
    public RelayCommand ExportCommand { get; }

    public SessionExportViewModel(string root, Func<bool> available, Func<string, string?> save)
    {
        _root = root; _available = available; _save = save;
        RefreshCommand = new(() => _ = RefreshAsync(), () => CanSelect);
        ExportCommand = new(() => _ = ExportSelectedAsync(), () => CanExport);
    }
    public void RefreshAvailability()
    {
        OnChanged(nameof(CanSelect)); OnChanged(nameof(CanExport));
        RefreshCommand?.Refresh(); ExportCommand?.Refresh();
    }
    public async Task RefreshAsync(string? preferredPath = null)
    {
        if (!CanSelect) return;
        string? previousId = SelectedSource?.Id;
        IsBusy = true;
        try
        {
            var warnings = new List<string>();
            var sources = await Task.Run(() => ReportTelemetryReader.Catalog(Path.Combine(_root, "Logs"), warnings));
            if (_disposed) return;
            Sources.Clear(); foreach (var source in sources) Sources.Add(source);
            SelectedSource = Sources.FirstOrDefault(s => s.FilePath == preferredPath) ?? Sources.FirstOrDefault(s => s.Id == previousId) ?? Sources.FirstOrDefault();
            Status = sources.Count == 0 ? "Пока нет сохранённых занятий." : "Экспорт доступен после завершения занятия. Аудио не включается.";
            if (warnings.Count > 0) Status += " " + string.Join(" ", warnings);
        }
        catch (Exception ex) { if (!_disposed) Status = "Не удалось обновить список: " + ex.Message; }
        finally { IsBusy = false; }
    }
    public async Task PreviewAsync()
    {
        int version = ++_selectionVersion; var source = SelectedSource;
        Preview = "";
        if (source is null || _disposed) return;
        try
        {
            var data = await Task.Run(() => ReportTelemetryReader.Read(source));
            if (_disposed || version != _selectionVersion) return;
            int notes = data.Events.Count(e => e.Type == "user_note");
            string? device = ReportJson.String(data.Events.FirstOrDefault(e => e.Type == "session_start")?.Data ?? default, "microphoneName");
            string state = data.Status switch { "completed" => "Завершено", "interrupted" => "Оборванный журнал", "practice_run" => "Практика без отдельной микрофонной сессии", _ => data.Status };
            Preview = $"{state} · {data.DurationMs / 1000.0:0.#} с · событий: {data.Events.Count} · заметок: {notes}" +
                (device is null ? "" : $"\nМикрофон: {device}") + (data.Warnings.Count == 0 ? "" : $"\nПредупреждений: {data.Warnings.Count}");
            foreach (var note in data.Events.Where(e => e.Type == "user_note").Take(3))
            {
                string text = ReportJson.String(note.Data, "text") ?? "";
                Preview += "\nЗаметка: " + (text.Length > 180 ? text[..180] + "…" : text);
            }
        }
        catch (Exception ex) { if (!_disposed && version == _selectionVersion) Preview = "Не удалось прочитать занятие: " + ex.Message; }
    }
    public async Task ExportSelectedAsync()
    {
        if (!CanExport || SelectedSource is not { } source) return;
        IsBusy = true;
        try
        {
            string suggested = "VocalTrainer_Session_" + (source.IsPractice ? source.RunId : source.Id.Replace("session-", "")) + ".zip";
            string? destination = _save(suggested);
            if (destination is null) return;
            // The modal save dialog can pump UI callbacks; recheck activity before reading the log.
            if (!_available()) { Status = "Сначала заверши активное занятие."; return; }
            var options = new ReportOptions(IncludeNotes, IncludeDeviceInfo, IncludeHistory);
            Status = "Создание отчёта…";
            var result = await Task.Run(() => SessionReportExporter.Export(source, destination, _root, options));
            if (!_disposed) Status = $"Сохранено: {result.FilePath}\nИзмерений: {result.PitchSamples}. " + string.Join(" ", result.Warnings);
        }
        catch (Exception ex) { if (!_disposed) Status = "Экспорт не выполнен: " + ex.Message; }
        finally { IsBusy = false; }
    }
    public void Dispose() { _disposed = true; ++_selectionVersion; RefreshAvailability(); }
}
