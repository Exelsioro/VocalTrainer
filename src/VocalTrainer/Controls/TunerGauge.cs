using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace VocalTrainer.Controls;

/// <summary>Pure presentation: -100..+100 cents, central ±25-cent accuracy zone.</summary>
public sealed class TunerGauge : FrameworkElement
{
    public static readonly DependencyProperty CentsProperty = DependencyProperty.Register(
        nameof(Cents), typeof(double), typeof(TunerGauge),
        new FrameworkPropertyMetadata(double.NaN, FrameworkPropertyMetadataOptions.AffectsRender));
    public double Cents { get => (double)GetValue(CentsProperty); set => SetValue(CentsProperty, value); }

    private static SolidColorBrush Brush(string hex)
    {
        var result = (SolidColorBrush)new BrushConverter().ConvertFrom(hex)!;
        result.Freeze();
        return result;
    }
    private static readonly Brush Muted = Brush("#8495B2");
    private static readonly Brush Central = Brush("#6EE5B1");
    private static readonly Brush Marker = Brush("#8DC4FF");
    private static readonly Brush Track = Brush("#2B3650");
    private static readonly Brush GoodZone = Brush("#193B37");

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        double w = ActualWidth, h = ActualHeight;
        if (w < 50 || h < 20) return;
        double inset = 13, available = w - inset * 2, mid = h * .42;
        double X(double cent) => inset + (cent + 100) / 200.0 * available;
        dc.DrawRoundedRectangle(Track, null, new Rect(inset, mid - 5, available, 10), 5, 5);
        dc.DrawRoundedRectangle(GoodZone, null, new Rect(X(-25), mid - 5, X(25) - X(-25), 10), 5, 5);
        for (int cents = -100; cents <= 100; cents += 25)
        {
            double x = X(cents);
            Brush brush = cents == 0 ? Central : Muted;
            dc.DrawLine(new Pen(brush, cents == 0 ? 2.5 : 1), new Point(x, mid - (cents == 0 ? 17 : 12)), new Point(x, mid + 14));
            var label = new FormattedText(cents > 0 ? $"+{cents}" : cents.ToString(), CultureInfo.CurrentUICulture,
                FlowDirection.LeftToRight, new Typeface("Segoe UI"), 11, Muted, VisualTreeHelper.GetDpi(this).PixelsPerDip);
            dc.DrawText(label, new Point(x - label.Width / 2, mid + 18));
        }
        if (double.IsFinite(Cents))
        {
            double x = X(Math.Clamp(Cents, -100, 100));
            dc.DrawEllipse(Math.Abs(Cents) <= 25 ? Central : Marker, new Pen(Brush("#0D1727"), 3), new Point(x, mid), 10, 10);
        }
    }
}
