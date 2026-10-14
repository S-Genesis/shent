using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Shent.Desktop.UI;

/// <summary>Anillo de progreso con degradado (suministro emitido, sincronización).</summary>
public sealed class ArcGauge : Control
{
    public static readonly StyledProperty<double> ValueProperty =
        AvaloniaProperty.Register<ArcGauge, double>(nameof(Value));

    public static readonly StyledProperty<double> StrokeWidthProperty =
        AvaloniaProperty.Register<ArcGauge, double>(nameof(StrokeWidth), 12);

    public static readonly StyledProperty<Color> StartColorProperty =
        AvaloniaProperty.Register<ArcGauge, Color>(nameof(StartColor), Color.Parse("#9B8CFF"));

    public static readonly StyledProperty<Color> EndColorProperty =
        AvaloniaProperty.Register<ArcGauge, Color>(nameof(EndColor), Color.Parse("#2BC4F0"));

    static ArcGauge()
    {
        AffectsRender<ArcGauge>(ValueProperty, StrokeWidthProperty, StartColorProperty, EndColorProperty);
    }

    public double Value
    {
        get => GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    public double StrokeWidth
    {
        get => GetValue(StrokeWidthProperty);
        set => SetValue(StrokeWidthProperty, value);
    }

    public Color StartColor
    {
        get => GetValue(StartColorProperty);
        set => SetValue(StartColorProperty, value);
    }

    public Color EndColor
    {
        get => GetValue(EndColorProperty);
        set => SetValue(EndColorProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        var size = Math.Min(Bounds.Width, Bounds.Height);
        if (size < StrokeWidth * 2) return;
        var center = new Point(Bounds.Width / 2, Bounds.Height / 2);
        var radius = (size - StrokeWidth) / 2;

        var track = new Pen(new SolidColorBrush(Color.FromArgb(28, 255, 255, 255)), StrokeWidth);
        context.DrawEllipse(null, track, center, radius, radius);

        var value = Math.Clamp(Value, 0, 1);
        if (value <= 0) return;
        var sweep = Math.Max(value * 360, 2);
        if (sweep >= 359.9) sweep = 359.9;

        var start = PointOnCircle(center, radius, -90);
        var end = PointOnCircle(center, radius, -90 + sweep);
        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            ctx.BeginFigure(start, false);
            ctx.ArcTo(end, new Size(radius, radius), 0, sweep > 180, SweepDirection.Clockwise);
            ctx.EndFigure(false);
        }

        var brush = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
            GradientStops = { new GradientStop(StartColor, 0), new GradientStop(EndColor, 1) },
        };
        context.DrawGeometry(null, new Pen(brush, StrokeWidth, lineCap: PenLineCap.Round), geometry);
    }

    private static Point PointOnCircle(Point center, double radius, double degrees)
    {
        var rad = degrees * Math.PI / 180;
        return new Point(center.X + radius * Math.Cos(rad), center.Y + radius * Math.Sin(rad));
    }
}
