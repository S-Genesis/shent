using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using QRCoder;

namespace Shent.Desktop.UI;

/// <summary>Código QR dibujado como vectores (nítido en cualquier escala de pantalla).</summary>
public sealed class QrCodeView : Control
{
    public static readonly StyledProperty<string?> TextProperty =
        AvaloniaProperty.Register<QrCodeView, string?>(nameof(Text));

    private bool[,]? _modules;

    static QrCodeView()
    {
        AffectsRender<QrCodeView>(TextProperty);
    }

    public string? Text
    {
        get => GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == TextProperty) _modules = Encode(Text);
    }

    private static bool[,]? Encode(string? text)
    {
        if (string.IsNullOrEmpty(text)) return null;
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(text.ToUpperInvariant(), QRCodeGenerator.ECCLevel.M);
        var matrix = data.ModuleMatrix;
        var size = matrix.Count;
        var modules = new bool[size, size];
        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++) modules[x, y] = matrix[y][x];
        }
        return modules;
    }

    public override void Render(DrawingContext context)
    {
        var side = Math.Min(Bounds.Width, Bounds.Height);
        context.DrawRectangle(Brushes.White, null, new RoundedRect(new Rect(0, 0, side, side), 18));
        if (_modules is null) return;

        var count = _modules.GetLength(0);
        var cell = side / count;
        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            for (var y = 0; y < count; y++)
            {
                for (var x = 0; x < count; x++)
                {
                    if (!_modules[x, y]) continue;
                    var r = new Rect(x * cell, y * cell, cell + 0.4, cell + 0.4);
                    ctx.BeginFigure(r.TopLeft, true);
                    ctx.LineTo(r.TopRight);
                    ctx.LineTo(r.BottomRight);
                    ctx.LineTo(r.BottomLeft);
                    ctx.EndFigure(true);
                }
            }
        }
        context.DrawGeometry(new SolidColorBrush(Color.Parse("#0B0E1A")), null, geometry);
    }
}
