using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Layout;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Shent.Core.Node;
using Shent.Desktop.Localization;
using Shent.Desktop.Services;
using Shent.Desktop.ViewModels;
using Shent.Desktop.Views;

namespace Shent.Desktop;

public partial class App : Application
{
    private FileStream? _instanceLock;
    private AppHost? _host;
    private MainViewModel? _vm;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var dataDir = DataDirectoryFromArgs(desktop.Args) ?? NodeConfig.DefaultDataDirectory();
            Directory.CreateDirectory(dataDir);
            Loc.SetLanguage(AppHost.LoadLanguage(dataDir) ?? Loc.SystemLanguage());
            if (!TryAcquireInstanceLock(dataDir))
            {
                desktop.MainWindow = AlreadyRunningWindow();
            }
            else
            {
                _host = new AppHost(dataDir);
                desktop.MainWindow = CreateMainWindow(_host);
                desktop.ShutdownRequested += async (_, _) =>
                {
                    _vm?.Dispose();
                    await _host.DisposeAsync();
                    _instanceLock?.Dispose();
                };
            }
        }
        base.OnFrameworkInitializationCompleted();
    }

    private MainWindow CreateMainWindow(AppHost host)
    {
        var window = new MainWindow();
        _vm = new MainViewModel(host, window);
        window.DataContext = _vm;
        return window;
    }

    /// <summary>
    /// Aplica otro idioma reconstruyendo la ventana con el mismo nodo y la misma billetera abierta:
    /// la red, la minería y la cadena siguen funcionando sin reiniciarse.
    /// </summary>
    public void ChangeLanguage(string code)
    {
        if (_host is null || ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop) return;
        if (desktop.MainWindow is not MainWindow old) return;
        _host.SaveLanguage(code);
        Loc.SetLanguage(code);

        var oldVm = _vm;
        var page = oldVm?.Pages.IndexOf(oldVm.CurrentPage) ?? 0;
        var window = CreateMainWindow(_host);
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Position = old.Position;
        window.Width = old.Width;
        window.Height = old.Height;
        window.WindowState = old.WindowState;
        if (page > 0 && page < _vm!.Pages.Count) _vm.CurrentPage = _vm.Pages[page];

        desktop.MainWindow = window;
        window.Show();
        oldVm?.Dispose();
        old.Close();
    }

    /// <summary>--datadir permite correr varios nodos en la misma PC (cada uno con su carpeta y su puerto).</summary>
    private static string? DataDirectoryFromArgs(string[]? args)
    {
        if (args is null) return null;
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i].StartsWith("--datadir=", StringComparison.Ordinal)) return Path.GetFullPath(args[i]["--datadir=".Length..]);
            if (args[i] == "--datadir" && i + 1 < args.Length) return Path.GetFullPath(args[i + 1]);
        }
        return null;
    }

    /// <summary>Un único proceso por carpeta de datos: dos nodos no pueden compartir la base ni el puerto.</summary>
    private bool TryAcquireInstanceLock(string dataDir)
    {
        try
        {
            _instanceLock = new FileStream(Path.Combine(dataDir, ".lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
    }

    private static Window AlreadyRunningWindow()
    {
        var close = new Button { Content = Loc.T("Cerrar"), HorizontalAlignment = HorizontalAlignment.Right, Classes = { "filled" } };
        var window = new Window
        {
            Title = "SHENT",
            Width = 440,
            Height = 200,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            Background = new SolidColorBrush(Color.Parse("#0A0D14")),
            Content = new StackPanel
            {
                Margin = new Thickness(28),
                Spacing = 14,
                Children =
                {
                    new TextBlock { Text = Loc.T("SHENT ya está abierto"), FontSize = 20, FontWeight = FontWeight.Bold },
                    new TextBlock
                    {
                        Text = Loc.T("Solo puede ejecutarse una instancia del nodo por usuario. Buscá la ventana abierta en la barra de tareas o el Dock."),
                        TextWrapping = TextWrapping.Wrap,
                        Foreground = new SolidColorBrush(Color.Parse("#9AA3C0")),
                    },
                    close,
                },
            },
        };
        close.Click += (_, _) => window.Close();
        return window;
    }
}
