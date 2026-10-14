using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Shent.Desktop.UI;

/// <summary>Gráfico de área suave con degradado, para el hashrate en tiempo real.</summary>
public sealed class SparklineChart : Control
{
    public static readonly StyledProperty<IReadOnlyList<double>?> ValuesProperty =
        AvaloniaProperty.Register<SparklineChart, IReadOnlyList<double>?>(nameof(Values));

    public static readonly StyledProperty<Color> LineColorProperty =
        AvaloniaProperty.Register<SparklineChart, Color>(nameof(LineColor), Color.Parse("#FFB547"));

    public static readonly StyledProperty<Color> LineColorEndProperty =
        AvaloniaProperty.Register<SparklineChart, Color>(nameof(LineColorEnd), Color.Parse("#F0517A"));

    public static readonly StyledProperty<bool> ShowGridProperty =
        AvaloniaProperty.Register<SparklineChart, bool>(nameof(ShowGrid), true);

    static SparklineChart()
    {
        AffectsRender<SparklineChart>(ValuesProperty, LineColorProperty, LineColorEndProperty, ShowGridProperty);
    }

    public IReadOnlyList<double>? Values
    {
        get => GetValue(ValuesProperty);
        set => SetValue(ValuesProperty, value);
    }

    public Color LineColor
    {
        get => GetValue(LineColorProperty);
        set => SetValue(LineColorProperty, value);
    }

    public Color LineColorEnd
    {
        get => GetValue(LineColorEndProperty);
        set => SetValue(LineColorEndProperty, value);
    }

    public bool ShowGrid
    {
        get => GetValue(ShowGridProperty);
        set => SetValue(ShowGridProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        var bounds = Bounds;
        var w = bounds.Width;
        var h = bounds.Height;
        if (w < 4 || h < 4) return;

        if (ShowGrid)
        {
            var gridPen = new Pen(new SolidColorBrush(Color.FromArgb(18, 255, 255, 255)), 1, new DashStyle([4, 6], 0));
            for (var i = 1; i <= 3; i++)
            {
                var y = Math.Round(h * i / 4.0) + 0.5;
                context.DrawLine(gridPen, new Point(0, y), new Point(w, y));
            }
        }

        var values = Values;
        if (values is null || values.Count < 2)
        {
            var idlePen = new Pen(new SolidColorBrush(Color.FromArgb(60, 255, 255, 255)), 2, new DashStyle([2, 6], 0), PenLineCap.Round);
            context.DrawLine(idlePen, new Point(0, h - 2), new Point(w, h - 2));
            return;
        }

        var max = values.Max();
        if (max <= 0) max = 1;
        max *= 1.15;
        var pad = 4.0;
        var points = new Point[values.Count];
        for (var i = 0; i < values.Count; i++)
        {
            var x = values.Count == 1 ? 0 : i * (w / (values.Count - 1));
            var y = pad + (h - pad * 2) * (1 - values[i] / max);
            points[i] = new Point(x, y);
        }

        var line = new StreamGeometry();
        using (var ctx = line.Open())
        {
            ctx.BeginFigure(points[0], false);
            AddSmoothCurve(ctx, points);
            ctx.EndFigure(false);
        }

        var area = new StreamGeometry();
        using (var ctx = area.Open())
        {
            ctx.BeginFigure(new Point(points[0].X, h), true);
            ctx.LineTo(points[0]);
            AddSmoothCurve(ctx, points);
            ctx.LineTo(new Point(points[^1].X, h));
            ctx.EndFigure(true);
        }

        var fill = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
            GradientStops =
            {
                new GradientStop(Color.FromArgb(110, LineColor.R, LineColor.G, LineColor.B), 0),
                new GradientStop(Color.FromArgb(0, LineColorEnd.R, LineColorEnd.G, LineColorEnd.B), 1),
            },
        };
        context.DrawGeometry(fill, null, area);

        var stroke = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(1, 0, RelativeUnit.Relative),
            GradientStops =
            {
                new GradientStop(LineColor, 0),
                new GradientStop(LineColorEnd, 1),
            },
        };
        context.DrawGeometry(null, new Pen(stroke, 2.5, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round), line);

        var last = points[^1];
        context.DrawEllipse(new SolidColorBrush(Color.FromArgb(60, LineColorEnd.R, LineColorEnd.G, LineColorEnd.B)), null, last, 9, 9);
        context.DrawEllipse(new SolidColorBrush(LineColorEnd), new Pen(Brushes.White, 2), last, 4.5, 4.5);
    }

    private static void AddSmoothCurve(StreamGeometryContext ctx, IReadOnlyList<Point> pts)
    {
        for (var i = 0; i < pts.Count - 1; i++)
        {
            var p0 = i > 0 ? pts[i - 1] : pts[i];
            var p1 = pts[i];
            var p2 = pts[i + 1];
            var p3 = i + 2 < pts.Count ? pts[i + 2] : p2;
            var c1 = new Point(p1.X + (p2.X - p0.X) / 6, p1.Y + (p2.Y - p0.Y) / 6);
            var c2 = new Point(p2.X - (p3.X - p1.X) / 6, p2.Y - (p3.Y - p1.Y) / 6);
            ctx.CubicBezierTo(c1, c2, p2);
        }
    }
}
