using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using VocalTrainer;
using VocalTrainer.ViewModels;
using VocalTrainer.Controls;
using VocalTrainer.Melody;

internal static class Program
{
    [STAThread]
    private static int Main()
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        var errors = new BindingErrors();
        PresentationTraceSources.DataBindingSource.Listeners.Add(errors);
        PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Error;
        MainWindow? window = null;
        try
        {
            // Exercise the real composition root and XAML without showing an interactive window.
            window = new MainWindow { ShowActivated = false, ShowInTaskbar = false, Opacity = 0 };
            window.Show();
            var vm = (MainViewModel)window.DataContext;
            var tabs = (TabControl)window.FindName("WorkspaceTabs");
            if (tabs.Items.Count != 7 || ((TabItem)tabs.Items[1]).Header?.ToString() != "Упражнения")
                throw new Exception("Exercises were not moved to a separate tab.");
            if (!vm.HasExercise) throw new Exception("Bundled exercise was not loaded.");
            vm.RangeDiagnostics.Checks.Insert(0, new("G2 — Соль (большая октава)", "+11 ¢", "82%", "17 ¢", "Неустойчиво / трудно"));
            vm.RangeDiagnostics.Checks.Insert(1, new("C3 — До (малая октава)", "−8 ¢", "96%", "9 ¢", "Комфортно"));
            var screenshotDirectory = Environment.GetEnvironmentVariable("VOCALTRAINER_SCREENSHOTS");
            for (int index = 0; index < tabs.Items.Count; index++)
            {
                vm.SelectedTabIndex = index;
                DrainDispatcher();
                window.UpdateLayout();
                // Force custom controls and templates to draw, including the MIDI piano roll.
                var bitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight,
                    96, 96, PixelFormats.Pbgra32);
                bitmap.Render(window);
                if (tabs.SelectedIndex != index) throw new Exception("Tab selection binding failed.");
                VerifyPageText(window, tabs);
                if (index == 0)
                {
                    VerifyOctaveSelection(window, vm);
                    VerifyFreeSinging(window, vm);
                    if (!string.IsNullOrWhiteSpace(screenshotDirectory)) SaveScreenshot(window, screenshotDirectory, "free-singing.png");
                }
                if (index == 1) VerifyExerciseLibrary(window, vm);
                if (index is 3 or 4)
                {
                    var table = (DataGrid)window.FindName(index == 3 ? "RangeResultsTable" : "ExerciseNotesTable");
                    VerifyTable(table, index == 4);
                }
                if (index == 5) { VerifySongLibrary(window, screenshotDirectory); VerifyMidiTraining(window, screenshotDirectory); }
                if (!string.IsNullOrWhiteSpace(screenshotDirectory)) SaveScreenshot(window, screenshotDirectory, $"tab-{index + 1}.png");
                Console.WriteLine($"OK WPF tab {index + 1}: {((TabItem)tabs.Items[index]).Header}");
            }
            if (errors.Messages.Count > 0)
                throw new Exception("WPF binding errors:\n" + string.Join("\n", errors.Messages));
            if (vm.SessionActive) throw new Exception("Microphone started without an explicit action.");
            window.Close();
            window = null;
            DrainDispatcher();
            Console.WriteLine("All WPF startup, binding, rendering and shutdown checks passed.");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            return 1;
        }
        finally
        {
            window?.Close();
            PresentationTraceSources.DataBindingSource.Listeners.Remove(errors);
            app.Shutdown();
        }
    }

    private static void DrainDispatcher()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle,
            new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T found) yield return found;
            foreach (var descendant in Descendants<T>(child)) yield return descendant;
        }
    }

    private static void VerifyPageText(MainWindow window, TabControl tabs)
    {
        // Regression: a selected tab's dark header foreground used to propagate to its page.
        foreach (var label in Descendants<TextBlock>(tabs).Where(t => t.IsVisible && t.FontSize >= 18 && !string.IsNullOrWhiteSpace(t.Text)))
            RequireContrast(label.Foreground, BackgroundBehind(label), $"page text '{label.Text}'");
        foreach (var button in Descendants<Button>(window).Where(b => b.IsVisible && !b.IsEnabled))
            RequireContrast(button.Foreground, button.Background, $"disabled button '{button.Content}'");
    }

    private static void VerifyOctaveSelection(MainWindow window, MainViewModel vm)
    {
        var selector = (ComboBox)window.FindName("OctaveSelector");
        if (selector.SelectedItem is not OctaveOption { Number: 3, Name: "малая октава" })
            throw new Exception("Initial octave selection or Russian name is incorrect.");
        selector.SelectedValue = 4;
        DrainDispatcher();
        if (vm.SelectedOctave != 4 || vm.NoteHeading != "C4")
            throw new Exception("Named octave selection did not update the target note.");
        vm.SelectedOctave = 3;
        DrainDispatcher();
        if (selector.SelectedItem is not OctaveOption { Number: 3 })
            throw new Exception("Programmatic octave changes did not update the selector.");
        Console.WriteLine("OK named octave selection preserves MIDI mapping in both directions");
    }

    private static void VerifyTable(DataGrid table, bool editable)
    {
        table.UpdateLayout();
        foreach (var header in Descendants<System.Windows.Controls.Primitives.DataGridColumnHeader>(table).Where(h => h.IsVisible))
            RequireContrast(header.Foreground, header.Background, "table header");
        if (table.Items.Count == 0) throw new Exception("Table regression test requires rows.");
        table.SelectedIndex = -1;
        DrainDispatcher();
        VerifyCells(table);
        table.SelectedIndex = 0;
        DrainDispatcher();
        VerifyCells(table);
        if (editable)
        {
            table.CurrentCell = new DataGridCellInfo(table.Items[0], table.Columns[0]);
            if (!table.BeginEdit()) throw new Exception("Styled table could not enter edit mode.");
            DrainDispatcher();
            var editor = Descendants<TextBox>(table).FirstOrDefault(t => t.IsVisible)
                ?? throw new Exception("Table editor was not created.");
            RequireContrast(editor.Foreground, editor.Background, "table text editor");
            table.CancelEdit();
            table.CancelEdit(DataGridEditingUnit.Row);
            DrainDispatcher();
        }
        Console.WriteLine($"OK table contrast: headers, rows, selection{(editable ? ", text editor" : "")}");
    }

    private static void VerifyExerciseLibrary(MainWindow window, MainViewModel vm)
    {
        if (vm.FreeSingingEnabled) throw new Exception("Entering exercises did not leave free singing mode.");
        var selector = (ComboBox)window.FindName("ExerciseSelector");
        if (selector.Items.Count < 4) throw new Exception("Bundled exercises are missing from the dropdown.");
        selector.SelectedIndex = 1;
        DrainDispatcher();
        if (vm.SelectedExercise?.Document.Title != vm.ExerciseTitle || vm.Timeline.Count == 0)
            throw new Exception("Selecting an exercise did not update its practice page.");
        int target = ExerciseIO.ParseMidi(vm.NoteHeading);
        vm.ApplyPitchMeasurement(new(NoteCatalog.ToFrequency(target) * Math.Pow(2, .1 / 12), .1, .99), 1000);
        DrainDispatcher();
        var gauge = (TunerGauge)window.FindName("ExerciseTunerGauge");
        if (Math.Abs(gauge.Cents - 10) > .01 || ((TextBlock)window.FindName("ExerciseDetectedNote")).Text != vm.NoteHeading)
            throw new Exception("Exercise tuner is not synchronized with live pitch and target.");
        vm.ActivateShortcut("tab6");
        if (vm.SelectedTabIndex != 5) throw new Exception("MIDI tab shortcut is out of sync.");
        vm.ActivateShortcut("tab2"); DrainDispatcher();
        if (vm.SelectedTabIndex != 1) throw new Exception("Exercises tab shortcut is out of sync.");
        Console.WriteLine("OK exercise tab, loaded dropdown, selection and revised tab shortcuts");
        Console.WriteLine("OK exercise tuner shares the selected target and live pitch");
    }

    private static void VerifyFreeSinging(MainWindow window, MainViewModel vm)
    {
        ((CheckBox)window.FindName("FreeSingingToggle")).IsChecked = true; DrainDispatcher();
        if (!vm.FreeSingingEnabled) throw new Exception("Free singing toggle is not bound.");
        vm.ApplyPitchMeasurement(new(NoteCatalog.ToFrequency(60), .1, .99), 0); DrainDispatcher();
        if (vm.TunerReferenceNote != "C4" || Math.Abs(vm.TunerCents) > .001)
            throw new Exception("Free tuner still uses the manually selected target.");
        vm.ApplyPitchMeasurement(new(NoteCatalog.ToFrequency(60) * Math.Pow(2, .4 / 12), .1, .99), 120);
        vm.ApplyPitchMeasurement(new(NoteCatalog.ToFrequency(62), .1, .99), 240); DrainDispatcher();
        var graph = (PianoRoll)window.FindName("FreeSingingGraph");
        if (graph.Voice?.Count != 3 || vm.FreeSinging.Note != "D4" || !vm.FreeSinging.Movement.Contains("Вверх"))
            throw new Exception("Free pitch, direction or history did not reach the graph.");
        vm.ApplyPitchMeasurement(new(null, .001, 0), 360); DrainDispatcher();
        if (!double.IsNaN(vm.TunerCents) || graph.Voice?[3].MidiPitch is not null)
            throw new Exception("Silence did not break the free singing trace.");
        Console.WriteLine("OK free singing UI: nearest-note tuner, pitch movement and silence gaps");
    }

    private static void VerifyCells(DataGrid table)
    {
        foreach (var label in Descendants<TextBlock>(table).Where(t => t.IsVisible && !string.IsNullOrWhiteSpace(t.Text)))
            RequireContrast(label.Foreground, BackgroundBehind(label), "table cell text");
    }

    private static void VerifySongLibrary(MainWindow window, string? screenshots)
    {
        string folder = Path.Combine(Path.GetTempPath(), "vocal-song-ui-" + Guid.NewGuid().ToString("N"));
        var workspace = (FrameworkElement)window.FindName("MelodyWorkspace");
        try
        {
            string source = Path.Combine(AppContext.BaseDirectory, "midi", "demo-vocal-melody.mid");
            using var songs = new MelodyViewModel((_, _) => { }, () => { }, () => true, () => false,
                () => source, (_, _) => { }, (_, ex) => throw ex, songLibrary: new SongLibrary(folder));
            workspace.DataContext = new { Melody = songs };
            songs.ImportCommand.Execute(null);
            songs.Lyrics.LoadText("[00:00]Сохранённая песня\n[00:02]Вторая строка", true, "Мой текст");
            songs.Transpose = 4;
            source = Path.Combine(AppContext.BaseDirectory, "midi", "demo-with-lyrics.mid");
            songs.ImportCommand.Execute(null); DrainDispatcher();
            var selector = (ComboBox)window.FindName("SavedSongsSelector");
            if (selector.Items.Count != 2 || songs.Transpose != 0 || songs.TransposeText != "0")
                throw new Exception("Song library dropdown or per-file pitch reset failed.");
            selector.SelectedIndex = 0; DrainDispatcher();
            if (songs.SelectedSong != songs.Songs[0] || songs.Lyrics.CurrentLine != "Сохранённая песня")
                throw new Exception("Song selection did not restore its associated lyrics.");
            selector.BringIntoView(); DrainDispatcher();
            if (!string.IsNullOrWhiteSpace(screenshots)) SaveScreenshot(window, screenshots, "song-library.png");
            Console.WriteLine("OK song library UI: saved dropdown, restored lyrics and pitch reset for another MIDI");
        }
        finally
        {
            workspace.ClearValue(FrameworkElement.DataContextProperty);
            if (Directory.Exists(folder)) Directory.Delete(folder, true);
        }
    }

    private static void VerifyMidiTraining(MainWindow window, string? screenshots)
    {
        var demo = MidiSongReader.Read(Path.Combine(AppContext.BaseDirectory, "midi", "demo-with-lyrics.mid"));
        if (demo.LyricTracks.Count != 1 || demo.LyricTracks[0].Lines.Count != 4)
            throw new Exception("Bundled MIDI lyrics demonstration is invalid.");
        using var melody = new MelodyViewModel((_, _) => { }, () => { }, () => true, () => true,
            () => null, (_, _) => { }, (_, ex) => throw ex);
        melody.SelectedTrack = new(0, "MIDI: проверка исполнения", [new(60, 0, 4, 0), new(62, 4, 4, 0), new(64, 8, 4, 0), new(67, 12, 48, 0)], 0);
        melody.Lyrics.LoadText("[00:00]Первая строка\n[00:00.150]Вторая строка\n[00:00.350]Третья строка", true, "Проверка LRC");
        ((FrameworkElement)window.FindName("MelodyWorkspace")).DataContext = new { Melody = melody };
        DrainDispatcher();
        var input = (TextBox)window.FindName("MelodyTransposeInput");
        input.Text = "+3";
        DrainDispatcher();
        var apply = (Button)window.FindName("ApplyMelodyTranspose");
        if (apply.Command?.CanExecute(null) != true) throw new Exception("Transpose button is unavailable for a loaded MIDI part.");
        apply.Command.Execute(null);
        DrainDispatcher();
        var plot = (PianoRoll)window.FindName("MelodyPianoRoll");
        if (melody.Transpose != 3 || plot.Targets?[0].Midi != 63) throw new Exception("Transpose UI did not update reference notes.");
        if (melody.ReferencePitches[0].Note != "D♯4" || !melody.ReferencePitches[0].Octave.Contains("1-я"))
            throw new Exception("Reference note and octave analysis did not reflect transpose.");
        ((CheckBox)window.FindName("SingWithReferenceToggle")).IsChecked = true;
        DrainDispatcher();
        if (!melody.SingWithReference) throw new Exception("Sing-with-reference mode is not bound.");
        var referenceTable = (DataGrid)window.FindName("ReferenceNotesTable");
        var referenceExpander = Descendants<Expander>(window).First(e => e.Header?.ToString()?.StartsWith("Ноты, октавы") == true);
        referenceExpander.IsExpanded = true;
        DrainDispatcher(); referenceTable.BringIntoView(); DrainDispatcher(); VerifyTable(referenceTable, false);
        if (!string.IsNullOrWhiteSpace(screenshots)) SaveScreenshot(window, screenshots, "reference-analysis.png");
        referenceExpander.IsExpanded = false;
        var timeline = (Slider)window.FindName("MelodyTimeline");
        timeline.Value = 25;
        DrainDispatcher();
        if (melody.FollowPlayhead || plot.ViewStart != 25) throw new Exception("Manual timeline scrolling is not synchronized.");
        double cursorBeforePan = melody.Cursor;
        plot.ScrollByWheel(-120); DrainDispatcher();
        if (melody.ViewStart <= 25 || melody.Cursor != cursorBeforePan) throw new Exception("Wheel panning moved playback or did not scroll.");
        double beforeDrag = melody.ViewStart;
        plot.PanByPixels((plot.ActualWidth - 74) / 4, beforeDrag); DrainDispatcher();
        if (Math.Abs(melody.ViewStart - (beforeDrag - 5)) > .01) throw new Exception("Grab panning did not move the viewport.");
        double clickedTime = melody.ViewStart + 10;
        plot.SeekAt(62 + (plot.ActualWidth - 74) / 2); DrainDispatcher();
        if (Math.Abs(melody.Cursor - clickedTime) > .01) throw new Exception("Piano roll click did not seek to its time coordinate.");
        Console.WriteLine("OK MIDI chart click-to-seek, grab panning and wheel navigation");
        melody.FollowPlayhead = true;
        melody.Lyrics.SelectedRow = melody.Lyrics.Rows[1];
        var lineButton = (Button)window.FindName("PracticeLyricLine");
        if (lineButton.Command?.CanExecute(null) != true) throw new Exception("Lyric practice button is unavailable.");
        lineButton.Command.Execute(null); DrainDispatcher();
        if (melody.FragmentFrom != "0.15" || melody.FragmentTo != "0.35") throw new Exception("Selecting a lyric line did not set the fragment.");
        melody.FragmentFrom = "0"; melody.FragmentTo = "60"; melody.PrepareCommand.Execute(null);
        melody.PracticeCommand.Execute(null);
        var wait = Stopwatch.StartNew();
        while (!melody.IsPracticing && wait.Elapsed.TotalSeconds < 7)
        { DrainDispatcher(); Thread.Sleep(15); }
        if (!melody.IsPracticing) throw new Exception("MIDI practice did not start after count-in.");
        foreach (double? midi in new double?[] { 63, 63.8, null, 51, 51.2, 63.2 })
        {
            Thread.Sleep(110);
            melody.OnMicrophoneSample(midi.HasValue ? 440 * Math.Pow(2, (midi.Value - 69) / 12) : null, midi.HasValue ? .99 : 0);
            DrainDispatcher();
        }
        if (!ReferenceEquals(plot.Voice, melody.Voice) || plot.Voice.Count != 6 || plot.Voice[2].MidiPitch is not null)
            throw new Exception("Microphone samples and gaps did not reach the bound piano roll.");
        var currentLine = (TextBlock)window.FindName("CurrentSongLine");
        if (currentLine.Text != "Третья строка" || !melody.Lyrics.Rows[2].IsCurrent)
            throw new Exception("Lyric line did not follow the practice clock.");
        melody.TogglePlaybackPause();
        string pausedLine = currentLine.Text;
        Thread.Sleep(120); melody.OnMicrophoneSample(NoteCatalog.ToFrequency(63), .99); DrainDispatcher();
        if (currentLine.Text != pausedLine || plot.Voice.Count != 6) throw new Exception("Paused lyrics or voice advanced.");
        melody.TogglePlaybackPause();
        melody.Stop();
        DrainDispatcher();
        var lyricsTable = (DataGrid)window.FindName("SongLyricsTable");
        lyricsTable.BringIntoView(); DrainDispatcher(); VerifyTable(lyricsTable, true);
        if (!string.IsNullOrWhiteSpace(screenshots)) SaveScreenshot(window, screenshots, "lyrics-editor.png");
        plot.BringIntoView();
        DrainDispatcher();
        var bitmap = new RenderTargetBitmap((int)plot.ActualWidth, (int)plot.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(plot);
        var pixels = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4];
        bitmap.CopyPixels(pixels, bitmap.PixelWidth * 4, 0);
        int green = 0, orange = 0;
        for (int i = 0; i < pixels.Length; i += 4)
        {
            if (Math.Abs(pixels[i] - 177) < 5 && Math.Abs(pixels[i + 1] - 229) < 5 && Math.Abs(pixels[i + 2] - 110) < 5) green++;
            if (Math.Abs(pixels[i] - 128) < 5 && Math.Abs(pixels[i + 1] - 202) < 5 && Math.Abs(pixels[i + 2] - 255) < 5) orange++;
        }
        if (green < 5 || orange < 5) throw new Exception("Accurate and off-target voice traces are missing from the rendered plot.");
        if (!string.IsNullOrWhiteSpace(screenshots))
        {
            Directory.CreateDirectory(screenshots);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var file = File.Create(Path.Combine(screenshots, "midi-performance.png"));
            encoder.Save(file);
        }
        Console.WriteLine("OK MIDI UI: transpose, scroll, synthetic microphone overlay, silence gaps and two trace colors");
        Console.WriteLine("OK lyrics UI: highlighted source line, next line, fragment selection and paused clock");
    }

    private static Brush BackgroundBehind(DependencyObject element)
    {
        for (var node = VisualTreeHelper.GetParent(element); node is not null; node = VisualTreeHelper.GetParent(node))
        {
            Brush? brush = node switch
            {
                Border border => border.Background,
                Panel panel => panel.Background,
                CheckBox => null, // Its Background paints the bullet, not the adjacent text label.
                Control control => control.Background,
                _ => null
            };
            if (brush is SolidColorBrush { Color.A: 255 }) return brush;
        }
        throw new Exception("Opaque background not found.");
    }

    private static void RequireContrast(Brush foreground, Brush background, string label)
    {
        if (foreground is not SolidColorBrush fg || background is not SolidColorBrush bg)
            throw new Exception($"Expected solid text colors: {label}");
        static double Luminance(Color color)
        {
            static double Channel(byte value)
            {
                double scaled = value / 255.0;
                return scaled <= .04045 ? scaled / 12.92 : Math.Pow((scaled + .055) / 1.055, 2.4);
            }
            return .2126 * Channel(color.R) + .7152 * Channel(color.G) + .0722 * Channel(color.B);
        }
        double a = Luminance(fg.Color), b = Luminance(bg.Color);
        double contrast = (Math.Max(a, b) + .05) / (Math.Min(a, b) + .05);
        if (contrast < 4.5) throw new Exception($"Insufficient contrast {contrast:F2}: {label}, {fg.Color} on {bg.Color}");
    }

    private static void SaveScreenshot(MainWindow window, string directory, string name)
    {
        Directory.CreateDirectory(directory);
        var content = (FrameworkElement)window.Content;
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(content.ActualWidth), (int)Math.Ceiling(content.ActualHeight),
            96, 96, PixelFormats.Pbgra32);
        bitmap.Render(content);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(Path.Combine(directory, name));
        encoder.Save(file);
    }

    private sealed class BindingErrors : TraceListener
    {
        public List<string> Messages { get; } = [];
        public override void Write(string? message) { if (message is not null) Messages.Add(message); }
        public override void WriteLine(string? message) => Write(message);
    }
}
