using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Input;
using VocalTrainer.Melody;

namespace VocalTrainer.Controls;

/// <summary>MIDI piano roll with pitch overlay, click-to-seek and grab/wheel navigation. Never stores audio.</summary>
public sealed class PianoRoll : FrameworkElement
{
    public static readonly DependencyProperty TargetsProperty = DependencyProperty.Register(nameof(Targets),
        typeof(IReadOnlyList<TimedTarget>), typeof(PianoRoll),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty VoiceProperty = DependencyProperty.Register(nameof(Voice),
        typeof(IReadOnlyList<VoiceSample>), typeof(PianoRoll),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty DurationProperty = DependencyProperty.Register(nameof(Duration),
        typeof(double), typeof(PianoRoll), new FrameworkPropertyMetadata(1.0, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty PlayheadSecondsProperty = DependencyProperty.Register(nameof(PlayheadSeconds),
        typeof(double), typeof(PianoRoll), new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty ViewStartProperty = DependencyProperty.Register(nameof(ViewStart),
        typeof(double), typeof(PianoRoll), new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty ViewWindowSecondsProperty = DependencyProperty.Register(nameof(ViewWindowSeconds),
        typeof(double), typeof(PianoRoll), new FrameworkPropertyMetadata(20.0, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty AccuracyToleranceCentsProperty = DependencyProperty.Register(nameof(AccuracyToleranceCents),
        typeof(double), typeof(PianoRoll), new FrameworkPropertyMetadata(40.0, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty SeekCommandProperty = DependencyProperty.Register(nameof(SeekCommand), typeof(ICommand), typeof(PianoRoll));
    public static readonly DependencyProperty PanCommandProperty = DependencyProperty.Register(nameof(PanCommand), typeof(ICommand), typeof(PianoRoll));
    public IReadOnlyList<TimedTarget>? Targets { get => (IReadOnlyList<TimedTarget>?)GetValue(TargetsProperty); set => SetValue(TargetsProperty, value); }
    public IReadOnlyList<VoiceSample>? Voice { get => (IReadOnlyList<VoiceSample>?)GetValue(VoiceProperty); set => SetValue(VoiceProperty, value); }
    public double Duration { get => (double)GetValue(DurationProperty); set => SetValue(DurationProperty, value); }
    public double PlayheadSeconds { get => (double)GetValue(PlayheadSecondsProperty); set => SetValue(PlayheadSecondsProperty, value); }
    public double ViewStart { get => (double)GetValue(ViewStartProperty); set => SetValue(ViewStartProperty, value); }
    public double ViewWindowSeconds { get => (double)GetValue(ViewWindowSecondsProperty); set => SetValue(ViewWindowSecondsProperty, value); }
    public double AccuracyToleranceCents { get => (double)GetValue(AccuracyToleranceCentsProperty); set => SetValue(AccuracyToleranceCentsProperty, value); }
    public ICommand? SeekCommand { get => (ICommand?)GetValue(SeekCommandProperty); set => SetValue(SeekCommandProperty, value); }
    public ICommand? PanCommand { get => (ICommand?)GetValue(PanCommandProperty); set => SetValue(PanCommandProperty, value); }
    private Point _pressPoint;
    private double _pressStart;
    private bool _pressed, _dragging;

    public double TimeAt(double x)
    {
        double duration = double.IsFinite(Duration) ? Math.Max(.1, Duration) : .1;
        double window = double.IsFinite(ViewWindowSeconds) ? Math.Clamp(ViewWindowSeconds, .1, duration) : duration;
        double start = Math.Clamp(double.IsFinite(ViewStart) ? ViewStart : 0, 0, Math.Max(0, duration - window));
        return start + Math.Clamp((x - Left) / Math.Max(1, ActualWidth - Left - Right), 0, 1) * window;
    }

    public void SeekAt(double x)
    {
        if (!double.IsFinite(x)) return;
        double seconds = TimeAt(x);
        if (SeekCommand?.CanExecute(seconds) == true) SeekCommand.Execute(seconds);
    }

    public void PanByPixels(double pixels, double initialStart)
    {
        if (!double.IsFinite(pixels) || !double.IsFinite(initialStart)) return;
        double seconds = initialStart - pixels * Math.Min(Duration, ViewWindowSeconds) / Math.Max(1, ActualWidth - Left - Right);
        if (PanCommand?.CanExecute(seconds) == true) PanCommand.Execute(seconds);
    }

    public void ScrollByWheel(int delta)
    {
        double seconds = ViewStart - delta / 120.0 * Math.Min(Duration, ViewWindowSeconds) / 6;
        if (PanCommand?.CanExecute(seconds) == true) PanCommand.Execute(seconds);
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (SeekCommand is null && PanCommand is null) return;
        var point = e.GetPosition(this);
        if (point.X < Left || point.X > ActualWidth - Right) return;
        Focusable = true;
        Focus();
        _pressPoint = point; _pressStart = ViewStart; _dragging = false;
        _pressed = CaptureMouse(); Cursor = Cursors.Hand;
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!_pressed) return;
        double delta = e.GetPosition(this).X - _pressPoint.X;
        if (Math.Abs(delta) >= SystemParameters.MinimumHorizontalDragDistance) _dragging = true;
        if (_dragging) PanByPixels(delta, _pressStart);
        e.Handled = true;
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (!_pressed) return;
        bool click = !_dragging; double x = e.GetPosition(this).X;
        _pressed = false; ReleaseMouseCapture();
        if (click) SeekAt(x);
        e.Handled = true;
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        _pressed = _dragging = false; Cursor = Cursors.Arrow;
        base.OnLostMouseCapture(e);
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        if (PanCommand is not null && Duration > ViewWindowSeconds)
        { ScrollByWheel(e.Delta); e.Handled = true; }
        else base.OnMouseWheel(e);
    }

    private static SolidColorBrush MakeBrush(string hex)
    {
        var result = (SolidColorBrush)new BrushConverter().ConvertFrom(hex)!;
        result.Freeze(); return result;
    }
    private static Pen MakePen(string color, double width)
    {
        var result = new Pen(MakeBrush(color), width); result.Freeze(); return result;
    }
    private static readonly Brush BackgroundBrush = MakeBrush("#0D1829");
    private static readonly Brush NoteBrush = MakeBrush("#315F8B");
    private static readonly Brush ActiveNoteBrush = MakeBrush("#477EAD");
    private static readonly Brush BlackKeyBrush = MakeBrush("#132238");
    private static readonly Brush TextBrush = MakeBrush("#9DB2CF");
    private static readonly Pen GridPen = MakePen("#27394C", 0.8);
    private static readonly Pen VoicePen = MakePen("#FFCA80", 2.6);
    private static readonly Pen AccuratePen = MakePen("#6EE5B1", 2.6);
    private static readonly Pen RestPen = MakePen("#B0BED0", 2.6);
    private static readonly Pen VoiceOutlinePen = MakePen("#0A111D", 5);
    private static readonly Pen DotOutlinePen = MakePen("#0A111D", 1);
    private static readonly Pen NoteOutlinePen = MakePen("#8CCBFA", 1);
    private static readonly Pen CursorPen = MakePen("#F7F9FD", 1.7);
    private const double Left = 62, Top = 16, Bottom = 29, Right = 12;

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        double width = ActualWidth, height = ActualHeight;
        if (width < 120 || height < 100) return;
        dc.DrawRoundedRectangle(BackgroundBrush, null, new Rect(0, 0, width, height), 14, 14);
        var rect = new Rect(Left, Top, width - Left - Right, height - Top - Bottom);
        dc.PushClip(new RectangleGeometry(new Rect(0, 0, width, height), 14, 14));
        var notes = Targets ?? Array.Empty<TimedTarget>();
        var voice = Voice ?? Array.Empty<VoiceSample>();
        var (minMidi, maxMidi) = VoicePlot.PitchBounds(notes, voice);
        if (maxMidi <= minMidi) maxMidi = minMidi + 1;
        double duration = double.IsFinite(Duration) ? Math.Max(.1, Duration) : 1;
        double window = double.IsFinite(ViewWindowSeconds) ? Math.Clamp(ViewWindowSeconds, .1, duration) : duration;
        double start = double.IsFinite(ViewStart) ? Math.Clamp(ViewStart, 0, Math.Max(0, duration - window)) : 0;
        double end = start + window;
        double X(double s) => rect.Left + rect.Width * (s - start) / window;
        double Y(double midi) => rect.Bottom - rect.Height * (midi - minMidi + .5) / (maxMidi - minMidi + 1);
        double rowHeight = rect.Height / (maxMidi - minMidi + 1);
        double tolerance = double.IsFinite(AccuracyToleranceCents) ? Math.Clamp(AccuracyToleranceCents, 1, 100) : 40;

        double ppd = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        for (int midi = minMidi; midi <= maxMidi; midi++)
        {
            double y = Y(midi);
            if (midi % 12 is 1 or 3 or 6 or 8 or 10)
                dc.DrawRectangle(BlackKeyBrush, null, new Rect(rect.Left, y - rowHeight / 2, rect.Width, rowHeight));
            dc.DrawLine(GridPen, new Point(rect.Left, y), new Point(rect.Right, y));
            if (midi % 12 == 0 || maxMidi - minMidi <= 18 || (maxMidi - minMidi <= 36 && midi % 2 == 0))
                DrawText(dc, NoteCatalog.ToLatin(midi), 13, y - 8, 10, ppd);
        }
        double tick = window <= 10 ? 1 : window <= 20 ? 2 : window <= 30 ? 5 : 10;
        for (double seconds = Math.Ceiling(start / tick) * tick; seconds <= end + 1e-6; seconds += tick)
        {
            double x = X(seconds);
            dc.DrawLine(GridPen, new Point(x, rect.Top), new Point(x, rect.Bottom));
            DrawText(dc, $"{seconds:0} с", Math.Min(x + 3, rect.Right - 30), rect.Bottom + 7, 10, ppd);
        }
        dc.PushClip(new RectangleGeometry(rect));
        foreach (var note in notes)
        {
            double left = X(note.StartSeconds), right = X(note.EndSeconds);
            if (right < rect.Left || left > rect.Right) continue;
            double y = Y(note.Midi);
            bool active = PlayheadSeconds >= note.StartSeconds && PlayheadSeconds < note.EndSeconds;
            double noteHeight = Math.Max(4, rowHeight * 2 * (note.ToleranceCents ?? tolerance) / 100);
            dc.DrawRoundedRectangle(active ? ActiveNoteBrush : NoteBrush, NoteOutlinePen,
                new Rect(left, y - noteHeight / 2, Math.Max(2, right - left - 1), noteHeight), 3, 3);
        }
        foreach (var stroke in VoicePlot.Strokes(voice, start, end))
        {
            Point? previous = null;
            VoiceSample? previousSample = null;
            foreach (var sample in stroke)
            {
                bool Accurate(VoiceSample point) => point.DeviationCents is { } cents && Math.Abs(cents) <= (point.ToleranceCents ?? tolerance);
                var pen = sample.DeviationCents is null ? RestPen : Accurate(sample) ? AccuratePen : VoicePen;
                var current = new Point(X(sample.Seconds), Y(sample.MidiPitch!.Value));
                if (previous is { } prior)
                {
                    var segmentPen = previousSample!.DeviationCents is not { } previousCents || sample.DeviationCents is not { } currentCents
                        ? RestPen : Accurate(previousSample) && Accurate(sample) ? AccuratePen : VoicePen;
                    dc.DrawLine(VoiceOutlinePen, prior, current);
                    dc.DrawLine(segmentPen, prior, current);
                }
                dc.DrawEllipse(pen.Brush, DotOutlinePen, current, 3, 3);
                previous = current;
                previousSample = sample;
            }
        }
        if (PlayheadSeconds >= start && PlayheadSeconds <= end)
        {
            double cursorX = X(PlayheadSeconds);
            dc.DrawLine(CursorPen, new Point(cursorX, rect.Top), new Point(cursorX, rect.Bottom));
        }
        dc.Pop();
        dc.Pop();
    }

    private static void DrawText(DrawingContext dc, string value, double x, double y, double fontSize, double ppd)
    {
        var text = new FormattedText(value, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface("Segoe UI"), fontSize, TextBrush, ppd);
        dc.DrawText(text, new Point(x, y));
    }
}
