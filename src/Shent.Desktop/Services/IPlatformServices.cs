namespace Shent.Desktop.Services;

/// <summary>Servicios que dependen de la ventana (diálogos de archivo y portapapeles).</summary>
public interface IPlatformServices
{
    Task<(string Name, string Content)?> OpenWalletFileAsync();
    Task<bool> SaveWalletFileAsync(string suggestedName, string content);
    Task CopyToClipboardAsync(string text);
    Task<string?> GetClipboardTextAsync();
    /// <summary>Copia un secreto y lo borra del portapapeles pasado un minuto si no cambió.</summary>
    Task CopySecretAsync(string text);
    void OpenFolder(string path);
}
