using System.Collections.ObjectModel;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Shent.Core.Node;
using Shent.Core.P2P;
using Shent.Desktop.Localization;
using Shent.Desktop.Services;
using Shent.Desktop.UI;

namespace Shent.Desktop.ViewModels;

public sealed partial class NetworkViewModel(MainViewModel shell) : PageViewModel(shell)
{
    public override string Title => Loc.T("Red P2P");
    public override string Subtitle => Loc.T("Pares conectados y descubrimiento de nodos");
    public override Geometry Icon => Icons.Network;

    public ObservableCollection<PeerItem> Peers { get; } = [];

    [ObservableProperty] private string _newPeer = "";
    [ObservableProperty] private bool _isAdding;
    [ObservableProperty] private string? _addError;

    [ObservableProperty] private string _listening = "";
    [ObservableProperty] private bool _isListening;
    [ObservableProperty] private string _port = "";
    [ObservableProperty] private string _lanPort = "";
    [ObservableProperty] private string _lanDiscovery = "";
    [ObservableProperty] private string _peerCount = "0";
    [ObservableProperty] private string _inbound = "0";
    [ObservableProperty] private string _outbound = "0";
    [ObservableProperty] private string _knownAddresses = "0";
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BannedText))]
    private string _banned = "0";
    [ObservableProperty] private string _bestHeight = "0";
    [ObservableProperty] private string _localHeight = "0";
    [ObservableProperty] private double _syncRatio;
    [ObservableProperty] private string _syncText = "";
    [ObservableProperty] private string _seedsText = "";
    [ObservableProperty] private bool _noPeers;

    public string BannedText => Loc.T("{0} bloqueados por mal comportamiento", Banned);

    public ObservableCollection<string> ShareAddresses { get; } = [];
    public ObservableCollection<string> LocalAddresses { get; } = [];
    [ObservableProperty] private string _peerId = "";
    [ObservableProperty] private string _reachabilityTitle = "";
    [ObservableProperty] private string _reachabilityText = "";
    [ObservableProperty] private bool _isReachable;
    [ObservableProperty] private bool _isReachabilityUnknown = true;
    [ObservableProperty] private string _upnpText = "";
    [ObservableProperty] private string _relayText = "";
    [ObservableProperty] private string _dhtText = "";
    [ObservableProperty] private string _privacyText = "";
    [ObservableProperty] private bool _hasShareAddresses;

    public override void Refresh()
    {
        var node = Shell.Host.Node;
        var status = node.GetStatus();
        PeerId = status.PeerId;
        IsReachable = status.Reachability == Reachability.Public;
        IsReachabilityUnknown = status.Reachability == Reachability.Unknown;
        (ReachabilityTitle, ReachabilityText) = status.Reachability switch
        {
            Reachability.Public => (Loc.T("Accesible desde Internet"),
                Loc.T("Otros nodos lograron conectarse a esta PC: la encuentran solos por la DHT. También podés compartir la dirección pública de abajo.")),
            Reachability.Private => (Loc.T("No accesible desde Internet"),
                !status.Listening
                    ? Loc.T("El nodo no acepta conexiones entrantes (listen desactivado). Otros nodos pueden llegar a esta PC solo a través de un relay.")
                    : status.CarrierNatDetected
                        ? Loc.T("Tu proveedor usa CGNAT: abrir puertos no alcanza. Otros nodos llegan a esta PC a través de un relay.")
                        : Loc.T("Nadie pudo conectarse al puerto {0}. Abrilo en el router y el firewall, o usá un relay (se busca uno automáticamente).", status.ListeningPort)),
            _ => (Loc.T("Comprobando alcanzabilidad"),
                Loc.T("Cuando haya pares conectados, le pediremos a uno que intente conectarse de vuelta a esta PC.")),
        };
        UpnpText = status.Upnp switch
        {
            UpnpState.Mapped => node.P2P.UpnpExternalIp is { } ip
                ? Loc.T("UPnP: puerto abierto en el router (IP del router {0})", ip)
                : Loc.T("UPnP: puerto abierto en el router"),
            UpnpState.Searching => Loc.T("UPnP: buscando router…"),
            UpnpState.NotFound => Loc.T("UPnP: no se encontró un router compatible"),
            UpnpState.Failed => Loc.T("UPnP: el router rechazó abrir el puerto"),
            _ => Loc.T("UPnP desactivado"),
        };
        DhtText = status.Dht switch
        {
            null or { Running: false } => Loc.T("Búsqueda por Internet desactivada"),
            { LastLookup: null } => Loc.T("Buscando nodos por Internet…"),
            { LastResponses: 0 } => Loc.T("Internet: la DHT no responde (¿UDP bloqueado?)"),
            { Announcing: true } d => Loc.T("Internet: visible en la DHT · {0} nodo(s) encontrados", Format.Integer(d.TotalAdded)),
            var d => Loc.T("Internet: {0} nodo(s) encontrados en la DHT", Format.Integer(d.TotalAdded)),
        };
        var dandelion = node.P2P.Dandelion;
        PrivacyText = (node.P2P.ProxyMode ? Loc.T("Por Tor ({0})", node.P2P.ProxyEndpoint) : Loc.T("Conexión directa")) + " · " +
                      (!dandelion.Enabled ? Loc.T("Dandelion++ desactivado")
                          : dandelion.StemPeers > 0 ? Loc.T("Dandelion++ con {0} par(es)", dandelion.StemPeers)
                          : Loc.T("Dandelion++ (esperando pares compatibles)"));
        RelayText = status.RelayService
            ? Loc.T("Relay activo: {0} reserva(s), {1} circuito(s)", node.P2P.Relay?.ReservationCount ?? 0, node.P2P.Relay?.ActiveCircuits ?? 0)
            : status.RelayReservations > 0
                ? Loc.T("Usando {0} relay(s) para recibir conexiones", status.RelayReservations)
                : Loc.T("Sin relay");
        SyncList(ShareAddresses, node.P2P.GetPublicAddresses());
        SyncList(LocalAddresses, node.P2P.GetLocalAddresses());
        HasShareAddresses = ShareAddresses.Count > 0;
        IsListening = status.Listening;
        Listening = status.Listening ? Loc.T("Aceptando conexiones entrantes") : Loc.T("Solo conexiones salientes");
        Port = $"{status.ListeningPort} / TCP";
        LanPort = $"{node.Network.LanDiscoveryPort} / UDP";
        LanDiscovery = status.LanDiscovery ? Loc.T("Activo en la red local") : Loc.T("Desactivado");
        PeerCount = status.PeersConnected.ToString();
        Inbound = status.Inbound.ToString();
        Outbound = status.Outbound.ToString();
        KnownAddresses = Format.Integer(status.KnownAddresses);
        Banned = status.BannedPeers.ToString();
        BestHeight = Format.Integer(status.BestKnownHeight);
        LocalHeight = Format.Integer(status.Height);
        SyncRatio = status.BestKnownHeight <= 0 ? 1 : Math.Min(1, (double)status.Height / status.BestKnownHeight);
        SyncText = status.Syncing ? Loc.T("Descargando bloques: {0}", Format.Percent(SyncRatio, 1)) : Loc.T("Cadena al día con los pares");
        var seeds = Shell.Host.Config.Seeds.Count + Shell.Host.Config.AddNodes.Count + Shell.Host.Config.Bootstrap.Count;
        SeedsText = node.P2P.ProxyMode
            ? Loc.T("Modo Tor: se usan los nodos guardados, los de arranque y los que comparten otros pares. La búsqueda por DHT no funciona por Tor.")
            : status.Dht is { Running: true }
            ? Loc.T("Buscando otros nodos SHENT por Internet y en la red local. No hace falta configurar nada: apenas aparezca un nodo accesible, se conecta solo.")
            : seeds == 0
                ? Loc.T("No hay nodos de arranque configurados. Pegá la dirección /ip4/…/p2p/… de otra PC o usá el descubrimiento en la red local.")
                : Loc.T("{0} nodo(s) de arranque configurado(s). Intentando conectar…", seeds);

        var snapshot = node.P2P.GetPeers().Where(p => p.HandshakeComplete).OrderByDescending(p => p.BestHeight).ToList();
        Peers.Clear();
        foreach (var p in snapshot) Peers.Add(PeerItem.From(p));
        NoPeers = Peers.Count == 0;
    }

    public void OnNodeRestarted() => Refresh();

    private static void SyncList(ObservableCollection<string> target, List<string> values)
    {
        if (target.SequenceEqual(values)) return;
        target.Clear();
        foreach (var v in values) target.Add(v);
    }

    [RelayCommand]
    private async Task CopyText(string? text)
    {
        if (string.IsNullOrEmpty(text)) return;
        await Shell.Platform.CopyToClipboardAsync(text);
        Shell.ShowToast(Loc.T("Copiado al portapapeles."), ToastKind.Success);
    }

    [RelayCommand]
    private async Task AddPeer()
    {
        var text = NewPeer.Trim();
        if (text.Length == 0)
        {
            AddError = Loc.T("Pegá una dirección como /ip4/IP_PUBLICA/tcp/37373/p2p/peer1… o IP:puerto.");
            return;
        }
        if (!AddressSpec.TryParse(text, Shell.Host.Node.Network.DefaultPort, out _, out var parseError))
        {
            AddError = Loc.Message(parseError);
            return;
        }
        IsAdding = true;
        AddError = null;
        try
        {
            var ok = await Shell.Host.Node.P2P.AddPeerAsync(text);
            if (ok)
            {
                NewPeer = "";
                Shell.ShowToast(Loc.T("Conectado con {0}.", text), ToastKind.Success);
                if (!Shell.Host.Config.AddNodes.Contains(text))
                {
                    Shell.Host.Config.AddNodes.Add(text);
                    try { Shell.Host.SaveConfig(); } catch (IOException) { }
                }
            }
            else
            {
                AddError = Loc.T("No se pudo conectar. Verificá la dirección, el puerto y el firewall del otro equipo.");
            }
        }
        finally
        {
            IsAdding = false;
            Refresh();
        }
    }

    [RelayCommand]
    private void Disconnect(PeerItem? peer)
    {
        if (peer is null) return;
        Shell.Host.Node.P2P.DisconnectPeer(peer.Id);
        Refresh();
    }
}
