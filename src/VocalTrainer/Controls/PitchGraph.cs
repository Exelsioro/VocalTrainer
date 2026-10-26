using System.Windows;
using System.Windows.Media;

namespace VocalTrainer.Controls;

public readonly record struct PitchPoint(long Milliseconds, double? Cents);

/// <summary>Lightweight WPF rendering-only component. Takes immutable history snapshots from the VM.</summary>
public sealed class PitchGraph : FrameworkElement
{
    public static readonly DependencyProperty PointsProperty = DependencyProperty.Register(
        nameof(Points), typeof(IReadOnlyList<PitchPoint>), typeof(PitchGraph),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public IReadOnlyList<PitchPoint>? Points
    {
        get => (IReadOnlyList<PitchPoint>?)GetValue(PointsProperty);
        set => SetValue(PointsProperty, value);
    }

    private static readonly Pen GridPen = MakePen("#2B3650", 1);
    private static readonly Pen CenterPen = MakePen("#3B806F", 1.5);
    private static readonly Pen SignalPen = MakePen("#8DC4FF", 2.5);
    private static readonly Brush ZoneBrush = Brush("#142E2B");
    private static readonly Brush SurfaceBrush = Brush("#0E192A");

    private static SolidColorBrush Brush(string hex) { var b = (SolidColorBrush)new BrushConverter().ConvertFrom(hex)!; b.Freeze(); return b; }
    private static Pen MakePen(string color, double width) { var pen = new Pen(Brush(color), width); pen.Freeze(); return pen; }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        double w = ActualWidth, h = ActualHeight;
        if (w < 10 || h < 10) return;
        dc.DrawRoundedRectangle(SurfaceBrush, null, new Rect(0, 0, w, h), 13, 13);
        dc.PushClip(new RectangleGeometry(new Rect(0, 0, w, h), 13, 13));
        // ±25 cents is the intended accuracy band.
        dc.DrawRectangle(ZoneBrush, null, new Rect(0, h * .375, w, h * .25));
        for (int cents = -100; cents <= 100; cents += 25)
        {
            double y = h * (100.0 - cents) / 200;
            dc.DrawLine(cents == 0 ? CenterPen : GridPen, new Point(0, y), new Point(w, y));
        }
        foreach (double xPart in new[] { .25, .5, .75 })
            dc.DrawLine(GridPen, new Point(w * xPart, 0), new Point(w * xPart, h));

        if (Points is { Count: > 1 } points)
        {
            long end = points[^1].Milliseconds;
            Point? previous = null;
            foreach (var point in points)
            {
                if (point.Cents is not { } cents || Math.Abs(cents) > 150)
                {
                    previous = null;
                    continue;
                }
                double x = w * (1 - (end - point.Milliseconds) / 30000.0);
                if (x < 0) { previous = null; continue; }
                var current = new Point(x, h * (100 - Math.Clamp(cents, -100, 100)) / 200);
                if (previous is { } earlier) dc.DrawLine(SignalPen, earlier, current);
                previous = current;
            }
        }
        dc.Pop();
    }
}
