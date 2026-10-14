using Avalonia.Markup.Xaml;

namespace Shent.Desktop.Localization;

/// <summary>Texto traducido en XAML: <c>Text="{l:T 'Abrir billetera'}"</c>. Se resuelve al crear la vista.</summary>
public sealed class TExtension : MarkupExtension
{
    public TExtension()
    {
    }

    public TExtension(string text) => Text = text;

    public string Text { get; set; } = "";

    public override object ProvideValue(IServiceProvider serviceProvider) => Loc.T(Text);
}
