using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Shent.Core.Consensus;
using Shent.Core.Node;
using Shent.Desktop.Localization;
using Shent.Desktop.UI;

namespace Shent.Desktop.ViewModels;

public sealed record NetworkOption(string Id, string Name, string Description)
{
    public override string ToString() => Name;
}

public sealed partial class SettingsViewModel : PageViewModel
{
    public override string Title => Loc.T("Ajustes");
    public override string Subtitle => Loc.T("Red, puertos y nodos iniciales");
    public override Geometry Icon => Icons.Cog;

    public IReadOnlyList<NetworkOption> Networks { get; } =
    [
        new("mainnet", Loc.T("Red principal (mainnet)"), Loc.T("Puerto {0}. La cadena compartida por la comunidad.", NetworkParams.Mainnet.DefaultPort)),
        new("testnet", Loc.T("Red de pruebas (testnet)"), Loc.T("Puerto {0}. Para probar sin afectar la red principal.", NetworkParams.Testnet.DefaultPort)),
        new("regtest", Loc.T("Regtest (solo local)"), Loc.T("Puerto {0}. Dificultad mínima para desarrollo.", NetworkParams.Regtest.DefaultPort)),
    ];

    [ObservableProperty] private NetworkOption _selectedNetwork;
    [ObservableProperty] private string _port = "";
    [ObservableProperty] private bool _listen;
    [ObservableProperty] private bool _lanDiscovery;
    [ObservableProperty] private bool _upnp;
    [ObservableProperty] private bool _dht;
    [ObservableProperty] private bool _relayClient;
    [ObservableProperty] private bool _relayService;
    [ObservableProperty] private bool _dandelion;
    [ObservableProperty] private bool _useProxy;
    [ObservableProperty] private string _proxy = "";
    [ObservableProperty] private decimal _maxOutbound;
    [ObservableProperty] private decimal _maxInbound;
    [ObservableProperty] private string _seeds = "";
    [ObservableProperty] private string _addNodes = "";
    [ObservableProperty] private string? _error;
    [ObservableProperty] private bool _isSaving;

    public string DataDirectory => Shell.Host.DataDirectory;
    public string ConfigPath => NodeConfig.ConfigPath(Shell.Host.DataDirectory);
    public string DefaultPortHint => Loc.T("Vacío o 0 = puerto por defecto ({0}).", NetworkParams.FromName(SelectedNetwork.Id).DefaultPort);

    public SettingsViewModel(MainViewModel shell) : base(shell)
    {
        _selectedNetwork = Networks[0];
        Load();
    }

    partial void OnSelectedNetworkChanged(NetworkOption value) => OnPropertyChanged(nameof(DefaultPortHint));

    public override void OnActivated()
    {
        Load();
        base.OnActivated();
    }

    private void Load()
    {
        var c = Shell.Host.Config;
        SelectedNetwork = Networks.FirstOrDefault(n => n.Id == c.Network) ?? Networks[0];
        Port = c.P2PPort > 0 ? c.P2PPort.ToString() : "";
        Listen = c.Listen;
        LanDiscovery = c.LanDiscovery;
        Upnp = c.Upnp;
        Dht = c.Dht;
        RelayClient = c.RelayClient;
        RelayService = c.RelayService;
        Dandelion = c.Dandelion;
        UseProxy = c.Proxy is not null;
        Proxy = c.Proxy ?? "127.0.0.1:9050";
        MaxOutbound = c.MaxOutbound;
        MaxInbound = c.MaxInbound;
        Seeds = string.Join(Environment.NewLine, c.Bootstrap.Concat(c.Seeds).Distinct());
        AddNodes = string.Join(Environment.NewLine, c.AddNodes);
        Error = null;
    }

    [RelayCommand]
    private async Task SaveAndRestart()
    {
        Error = null;
        var current = Shell.Host.Config;
        var port = 0;
        if (!string.IsNullOrWhiteSpace(Port) && (!int.TryParse(Port.Trim(), out port) || port is < 1024 or > 65535))
        {
            Error = Loc.T("El puerto debe ser un número entre 1024 y 65535.");
            return;
        }

        if (UseProxy && !Shent.Core.P2P.P2PNetwork.TryParseProxy(Proxy, out _))
        {
            Error = Loc.T("Proxy inválido. Usá IP:puerto, por ejemplo 127.0.0.1:9050 (Tor) o 127.0.0.1:9150 (Tor Browser).");
            return;
        }

        var seeds = SplitLines(Seeds);
        var addNodes = SplitLines(AddNodes);
        var network = NetworkParams.FromName(SelectedNetwork.Id);
        foreach (var entry in seeds.Concat(addNodes))
        {
            if (!Shent.Core.P2P.AddressSpec.TryParse(entry, network.DefaultPort, out _, out var parseError))
            {
                Error = Loc.T("Dirección inválida «{0}»: {1}", entry, Loc.Message(parseError));
                return;
            }
        }

        var config = new NodeConfig
        {
            Network = SelectedNetwork.Id,
            DataDirectory = current.DataDirectory,
            P2PPort = port,
            Listen = Listen,
            LanDiscovery = LanDiscovery,
            Upnp = Upnp,
            Dht = Dht,
            DhtRouters = current.DhtRouters,
            RelayClient = RelayClient,
            RelayService = RelayService,
            Announce = current.Announce,
            Dandelion = Dandelion,
            Proxy = UseProxy ? Proxy.Trim() : null,
            Api = current.Api,
            ApiPort = current.ApiPort,
            MaxOutbound = (int)MaxOutbound,
            MaxInbound = (int)MaxInbound,
            Bootstrap = seeds,
            AddNodes = addNodes,
            Mine = current.Mine,
            MiningThreads = current.MiningThreads,
            MiningIntensity = current.MiningIntensity,
            MiningAddress = current.Network == SelectedNetwork.Id ? current.MiningAddress : null,
        };

        IsSaving = true;
        try
        {
            await Shell.Host.RestartAsync(config);
            Shell.ShowToast(Loc.T("Configuración guardada. El nodo se reinició."), ToastKind.Success);
        }
        catch (Exception ex) when (ex is IOException or System.Net.Sockets.SocketException or InvalidOperationException)
        {
            Error = Loc.T("No se pudo reiniciar el nodo: {0}", Loc.Message(ex.Message));
        }
        finally
        {
            IsSaving = false;
            Load();
        }
    }

    [RelayCommand]
    private void Revert() => Load();

    [RelayCommand]
    private void OpenDataFolder() => Shell.Platform.OpenFolder(Shell.Host.DataDirectory);

    private static List<string> SplitLines(string text) => text
        .Split(['\r', '\n', ',', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToList();
}
