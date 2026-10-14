using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Platform.Storage;
using Shent.Desktop.Localization;
using Shent.Desktop.Services;
using Shent.Desktop.ViewModels;

namespace Shent.Desktop.Views;

public partial class MainWindow : Window, IPlatformServices
{
    private static FilePickerFileType WalletFileType() => new(Loc.T("Respaldo de billetera SHENT"))
    {
        Patterns = ["*.shent"],
        AppleUniformTypeIdentifiers = ["public.json", "public.data"],
        MimeTypes = ["application/json"],
    };

    private const long MaxWalletFileBytes = 64 * 1024;

    public MainWindow()
    {
        InitializeComponent();
        Opened += (_, _) => FitToScreen();
    }

    /// <summary>En pantallas chicas (o con escalado alto) la ventana por defecto no entra: se maximiza.</summary>
    private void FitToScreen()
    {
        var screen = Screens.ScreenFromWindow(this) ?? Screens.Primary;
        if (screen is null) return;
        var area = screen.WorkingArea.Size.ToSize(screen.Scaling);
        if (area.Width < Width || area.Height < Height) WindowState = WindowState.Maximized;
    }

    private void OnBackdropPressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is MainViewModel vm) vm.DismissDialogCommand.Execute(null);
    }

    private void OnToastPressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is MainViewModel vm) vm.HideToastCommand.Execute(null);
    }

    public async Task<(string Name, string Content)?> OpenWalletFileAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = Loc.T("Elegí el respaldo de tu billetera"),
            AllowMultiple = false,
            FileTypeFilter = [WalletFileType(), FilePickerFileTypes.All],
        });
        if (files.Count == 0) return null;
        var file = files[0];
        var props = await file.GetBasicPropertiesAsync();
        if (props.Size is > MaxWalletFileBytes) return (file.Name, "");
        await using var stream = await file.OpenReadAsync();
        using var reader = new StreamReader(stream);
        var content = await reader.ReadToEndAsync();
        return (file.Name, content);
    }

    public async Task<bool> SaveWalletFileAsync(string suggestedName, string content)
    {
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = Loc.T("Guardar respaldo cifrado"),
            SuggestedFileName = suggestedName,
            DefaultExtension = "shent",
            FileTypeChoices = [WalletFileType()],
            ShowOverwritePrompt = true,
        });
        if (file is null) return false;
        await using var stream = await file.OpenWriteAsync();
        stream.SetLength(0);
        await using var writer = new StreamWriter(stream);
        await writer.WriteAsync(content);
        return true;
    }

    public async Task CopyToClipboardAsync(string text)
    {
        if (Clipboard is { } clipboard) await clipboard.SetTextAsync(text);
    }

    public async Task<string?> GetClipboardTextAsync()
    {
        return Clipboard is { } clipboard ? await clipboard.TryGetTextAsync() : null;
    }

    public async Task CopySecretAsync(string text)
    {
        if (Clipboard is not { } clipboard) return;
        await clipboard.SetTextAsync(text);
        _ = Task.Delay(TimeSpan.FromSeconds(60)).ContinueWith(async _ =>
        {
            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(async () =>
            {
                var current = await clipboard.TryGetTextAsync();
                if (current == text) await clipboard.ClearAsync();
            });
        });
    }

    public void OpenFolder(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
            if (OperatingSystem.IsWindows())
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
            }
            else if (OperatingSystem.IsMacOS())
            {
                Process.Start("open", [path]);
            }
            else
            {
                Process.Start("xdg-open", [path]);
            }
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException)
        {
            if (DataContext is MainViewModel vm) vm.ShowToast(Loc.T("No se pudo abrir la carpeta: {0}", path), ToastKind.Error);
        }
    }
}
