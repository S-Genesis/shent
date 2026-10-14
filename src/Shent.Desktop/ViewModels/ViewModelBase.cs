using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Shent.Desktop.ViewModels;

public abstract class ViewModelBase : ObservableObject;

/// <summary>Página principal navegable desde la barra lateral.</summary>
public abstract class PageViewModel(MainViewModel shell) : ViewModelBase
{
    protected MainViewModel Shell { get; } = shell;
    public abstract string Title { get; }
    public abstract string Subtitle { get; }
    public abstract Geometry Icon { get; }

    /// <summary>Se invoca una vez por segundo mientras la página está visible.</summary>
    public virtual void Refresh() { }

    public virtual void OnActivated() => Refresh();
}

/// <summary>Diálogo modal mostrado sobre la ventana principal.</summary>
public abstract partial class DialogViewModel(MainViewModel shell) : ViewModelBase
{
    protected MainViewModel Shell { get; } = shell;

    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string? _error;

    public abstract string Title { get; }

    public virtual void Close() => Shell.CloseDialog(this);

    public virtual void OnClosed() { }
}
