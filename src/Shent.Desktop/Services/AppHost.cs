using Shent.Core.Chain;
using Shent.Core.Node;
using Shent.Core.Wallet;

namespace Shent.Desktop.Services;

/// <summary>Estado global de la aplicación: configuración, nodo en ejecución y billetera desbloqueada.</summary>
public sealed class AppHost : IAsyncDisposable
{
    public string DataDirectory { get; }
    public NodeConfig Config { get; private set; }
    public NodeLog Log { get; } = new();
    public ShentNode Node { get; private set; }
    public WalletManager Wallets { get; private set; }
    public UnlockedWallet? Wallet { get; private set; }
    public ChainStatistics Stats { get; private set; }

    public event Action? WalletChanged;
    public event Action? NodeRestarted;

    public AppHost(string dataDirectory)
    {
        DataDirectory = dataDirectory;
        Config = NodeConfig.Load(dataDirectory);
        Config.UserAgent = $"SHENT-Desktop/{typeof(AppHost).Assembly.GetName().Version?.ToString(3) ?? "0.1.0"}";
        if (!File.Exists(NodeConfig.ConfigPath(dataDirectory))) Config.Save();
        Node = ShentNode.Start(Config, Log);
        Stats = new ChainStatistics(Node.Chain);
        Wallets = new WalletManager(Node.Network, Config.NetworkDataDirectory);
    }

    public void SetWallet(UnlockedWallet? wallet)
    {
        if (!ReferenceEquals(Wallet, wallet)) Wallet?.Dispose();
        Wallet = wallet;
        DiscoverWalletAddresses();
        WalletChanged?.Invoke();
    }

    /// <summary>Busca en la cadena direcciones de cobro de la billetera usadas más allá de las conocidas.</summary>
    public bool DiscoverWalletAddresses()
    {
        if (Wallet is not { IsLocked: false } wallet) return false;
        var chain = Node.Chain;
        return Wallets.DiscoverAddresses(wallet, address =>
        {
            var account = chain.GetAccount(address);
            return account.Balance > 0 || account.Nonce > 0;
        });
    }

    public WalletTotals? WalletTotals() =>
        Wallet is { IsLocked: false } wallet ? WalletBalances.Compute(wallet, Node.Chain, Node.Mempool) : null;

    public void SaveConfig() => Config.Save();

    private static string UiSettingsPath(string dataDirectory) => Path.Combine(dataDirectory, "ui.json");

    /// <summary>Idioma guardado de la interfaz, o null si la persona todavía no eligió uno.</summary>
    public static string? LoadLanguage(string dataDirectory)
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(UiSettingsPath(dataDirectory)));
            return doc.RootElement.TryGetProperty("language", out var value) ? value.GetString() : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidOperationException)
        {
            return null;
        }
    }

    public void SaveLanguage(string code)
    {
        try
        {
            File.WriteAllText(UiSettingsPath(DataDirectory), System.Text.Json.JsonSerializer.Serialize(new { language = code }));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    public async Task RestartAsync(NodeConfig newConfig)
    {
        var networkChanged = newConfig.Network != Config.Network;
        await Node.DisposeAsync();
        newConfig.UserAgent = Config.UserAgent;
        Config = newConfig;
        Config.Save();
        Node = ShentNode.Start(Config, Log);
        Stats = new ChainStatistics(Node.Chain);
        if (networkChanged)
        {
            Wallets = new WalletManager(Node.Network, Config.NetworkDataDirectory);
            SetWallet(null);
        }
        NodeRestarted?.Invoke();
    }

    public async ValueTask DisposeAsync()
    {
        Wallet?.Dispose();
        await Node.DisposeAsync();
    }
}
