using System.Collections.Concurrent;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Numerics;
using System.Security.Cryptography;
using Shent.Core.Chain;
using Shent.Core.Consensus;
using Shent.Core.Crypto;
using Shent.Core.Mempool;
using Shent.Core.Model;
using Shent.Core.Node;

namespace Shent.Core.P2P;

public sealed record PeerSnapshot(
    Guid Id, string Address, bool Inbound, string UserAgent, long BestHeight, double? PingMs,
    DateTimeOffset ConnectedAt, bool Syncing, int Misbehavior, bool HandshakeComplete,
    string? PeerId = null, bool Relayed = false, NodeServices Services = NodeServices.None, string? TipHash = null);

/// <summary>Resultado de la prueba de alcanzabilidad (dial-back) del puerto P2P de este nodo.</summary>
public enum Reachability
{
    /// <summary>Todavía no se pudo comprobar (faltan pares que hagan la prueba).</summary>
    Unknown,
    /// <summary>Otros nodos lograron conectarse a este nodo desde Internet.</summary>
    Public,
    /// <summary>Nadie pudo conectarse: NAT, CGNAT o firewall. Se usa un relay para recibir conexiones.</summary>
    Private,
}

public sealed record RelayReservationInfo(string RelayPeerId, string RelayAddress, string CircuitAddress, DateTimeOffset Expires);

/// <summary>Estado del descubrimiento por la DHT. Las direcciones encontradas no son de confianza hasta el handshake.</summary>
public sealed record DhtStatus(bool Running, DateTimeOffset? LastLookup, int LastPeersFound, int LastResponses, bool Announcing, int TotalAdded);

/// <summary>Estado de Dandelion++: pares elegidos para la fase de tallo y transacciones todavía no difundidas.</summary>
public sealed record DandelionStatus(bool Enabled, int StemPeers, int PrivateTransactions);

/// <summary>
/// Red P2P de un nodo SHENT. Ningún par es de confianza: todo bloque y transacción recibida se valida
/// localmente con las reglas de consenso, y la cadena elegida es la válida con mayor trabajo acumulado,
/// no la que anuncian más pares ni la que provee un nodo de arranque, un relay o un servidor conocido.
///
/// Cada nodo tiene una identidad Ed25519 persistente (Peer ID). En el handshake ambos lados firman un
/// desafío aleatorio del otro, así se detectan conexiones duplicadas, conexiones con uno mismo y nodos
/// que no son quienes dicen ser (cuando se marca una dirección /p2p/PEER_ID).
/// </summary>
public sealed class P2PNetwork : IAsyncDisposable
{
    private const int MaxOrphans = 200;
    private const int MaxPerIpInbound = 4;
    private const int MaxPendingInbound = 64;
    private const int BlocksPerRequest = 128;
    private const int MaxRelayReservations = 2;
    private const int MaxConcurrentDialBacks = 4;
    private const int MaxRecentRejects = 10_000;
    private static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan FirstFrameTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan IdleTimeout = TimeSpan.FromSeconds(180);
    private static readonly TimeSpan PingInterval = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan SyncStallTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan BanDuration = TimeSpan.FromHours(24);
    private static readonly TimeSpan DialBackServeInterval = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan TxRequestTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan DandelionEpoch = TimeSpan.FromMinutes(10);
    private const int StemPeerCount = 2;
    /// <summary>Probabilidad de que un nodo sea "difusor" en una época (Dandelion++: largo medio del tallo ≈ 10 saltos).</summary>
    private const double DiffuserProbability = 0.1;

    private sealed class ProbeState(Guid peerId)
    {
        public Guid PeerId { get; } = peerId;
        public DateTimeOffset Created { get; } = DateTimeOffset.UtcNow;
        public volatile bool Received;
    }

    private sealed record ClientReservation(PeerConnection Relay, PeerAddress RelayEndpoint, DateTimeOffset Expires);

    private readonly NodeConfig _config;
    private readonly NetworkParams _network;
    private readonly Blockchain _chain;
    private readonly TxMempool _mempool;
    private readonly NodeLog _log;
    private readonly AddressBook _book;
    private readonly NodeIdentity _identity;
    private readonly bool _ownsIdentity;
    private readonly RelayService? _relay;
    private readonly ConcurrentDictionary<Guid, PeerConnection> _peers = new();
    private readonly ConcurrentDictionary<string, byte> _connecting = new();
    /// <summary>Direcciones escritas por el usuario (se permiten aunque sean 127.0.0.1, p. ej. dos nodos en la misma PC).</summary>
    private readonly ConcurrentDictionary<string, byte> _manualKeys = new();
    private readonly ConcurrentDictionary<Hash256, ProbeState> _pendingProbes = new();
    private readonly ConcurrentDictionary<Hash256, long> _txRequested = new();
    private readonly ConcurrentDictionary<Guid, ClientReservation> _reservations = new();
    private readonly ConcurrentDictionary<Guid, DateTimeOffset> _reservationRequests = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly object _orphanGate = new();
    private readonly Dictionary<Hash256, Block> _orphans = new();
    private readonly object _syncGate = new();
    private readonly object _rejectGate = new();
    private readonly HashSet<Hash256> _recentRejects = new();
    private readonly object _reachGate = new();
    private readonly List<NodeAddress> _announced = new();
    private PeerConnection? _syncPeer;
    private TcpListener? _listener;
    private LanDiscovery? _lan;
    private MainlineDht? _dht;
    private readonly ConcurrentDictionary<string, byte> _selfKeys = new();
    private DhtStatus _dhtStatus = new(false, null, 0, 0, false, 0);
    private UpnpPortMapper? _upnp;
    private readonly IPEndPoint? _proxy;
    private readonly object _dandelionGate = new();
    private readonly List<PeerConnection> _stemPeers = new();
    private readonly Dictionary<Guid, PeerConnection> _stemRoutes = new();
    private readonly ConcurrentDictionary<Hash256, long> _embargo = new();
    private DateTimeOffset _epochEnd = DateTimeOffset.MinValue;
    private bool _diffuser;
    private DateTimeOffset _lastSeedQuery = DateTimeOffset.MinValue;
    private DateTimeOffset _lastBookSave = DateTimeOffset.UtcNow;
    private DateTimeOffset _lastPeerExchange = DateTimeOffset.UtcNow;
    private DateTimeOffset _nextDialBackRound = DateTimeOffset.MinValue;
    private int _roundRequested;
    private int _roundFailures;
    private int _roundSuccesses;
    private int _dialBacksInFlight;
    private int _pendingInbound;
    private bool _cgnatWarned;

    public ulong NodeNonce { get; } = BitConverter.ToUInt64(RandomNumberGenerator.GetBytes(8));
    public PeerId PeerId => _identity.PeerId;
    public int ListeningPort { get; private set; }
    public bool IsListening => _listener is not null;
    public bool LanDiscoveryActive => _lan?.Active == true;
    public AddressBook AddressBook => _book;
    public Reachability Reachability { get; private set; }
    /// <summary>Dirección pública verificada por dial-back (null si no se comprobó).</summary>
    public PeerAddress? PublicAddress { get; private set; }
    public UpnpState UpnpState => _upnp?.State ?? UpnpState.Disabled;
    public IPAddress? UpnpExternalIp => _upnp?.ExternalIp;
    /// <summary>Hay indicios de CGNAT: el router recibe una IP privada o de 100.64.0.0/10.</summary>
    public bool CarrierNatDetected => _upnp?.BehindCarrierNat == true;
    public RelayService? Relay => _relay;
    public DhtStatus Dht => _dhtStatus;
    /// <summary>Todas las conexiones salen por un proxy SOCKS5 (Tor) y no se revela la IP real.</summary>
    public bool ProxyMode => _proxy is not null;
    public string? ProxyEndpoint => _proxy?.ToString();

    public DandelionStatus Dandelion
    {
        get
        {
            int stems;
            lock (_dandelionGate) stems = _stemPeers.Count(p => !p.IsClosed);
            return new DandelionStatus(_config.Dandelion, stems, _mempool.GetPrivateIds().Count);
        }
    }

    public event Action? PeersChanged;

    public P2PNetwork(NodeConfig config, NetworkParams network, Blockchain chain, TxMempool mempool, NodeLog log,
        string? addressBookPath, NodeIdentity? identity = null)
    {
        _config = config;
        _network = network;
        _chain = chain;
        _mempool = mempool;
        _log = log;
        _book = new AddressBook(addressBookPath);
        _ownsIdentity = identity is null;
        _identity = identity ?? NodeIdentity.Ephemeral();
        if (config.RelayService) _relay = new RelayService(network, log);
        if (config.Proxy is { } proxyText)
        {
            if (TryParseProxy(proxyText, out var proxy))
            {
                _proxy = proxy;
            }
            else
            {
                // Sin un proxy válido no se cae a conexiones directas: eso expondría la IP que el usuario quiere ocultar.
                _proxy = new IPEndPoint(IPAddress.Loopback, 0);
                log.Error("p2p", $"Proxy inválido «{proxyText}» (usá host:puerto, p. ej. 127.0.0.1:9050). No se harán conexiones hasta corregirlo, para no exponer tu IP.");
            }
        }
        _chain.ChainUpdated += OnChainUpdated;
    }

    public static bool TryParseProxy(string text, out IPEndPoint endpoint)
    {
        text = text.Trim();
        if (text.StartsWith("localhost:", StringComparison.OrdinalIgnoreCase)) text = "127.0.0.1" + text["localhost".Length..];
        if (IPEndPoint.TryParse(text, out var parsed) && parsed.Port is > 0 and <= 65535)
        {
            endpoint = parsed;
            return true;
        }
        endpoint = new IPEndPoint(IPAddress.Loopback, 0);
        return false;
    }

    public void Start()
    {
        _log.Info("p2p", $"Peer ID de este nodo: {PeerId}");
        foreach (var node in _config.Bootstrap) AddConfigured(node, "bootstrap");
        foreach (var node in _config.AddNodes) AddConfigured(node, "manual");

        if (ProxyMode)
        {
            _log.Info("p2p", $"Modo privado: todas las conexiones salen por el proxy {_proxy}. " +
                             "Escucha, UPnP, DHT y red local quedan desactivados porque revelarían tu IP real.");
            if (_book.Count == 0 && _config.Bootstrap.Count == 0 && _config.AddNodes.Count == 0 && _config.Seeds.Count == 0 && _network.Seeds.Count == 0)
            {
                _log.Warn("p2p", "No hay nodos guardados y la DHT no funciona por Tor. Abrí SHENT una vez sin proxy para conocer nodos, " +
                                 "o agregá uno con addnode/bootstrap.");
            }
        }
        if (_config.Listen && !ProxyMode) StartListener();
        if (!IsListening) Reachability = Reachability.Private;
        if (_config.LanDiscovery && IsListening)
        {
            _lan = new LanDiscovery(_network.Magic, NodeNonce, _network.LanDiscoveryPort, () => ListeningPort, OnLanPeer);
            if (!_lan.Start(m => _log.Warn("lan", m))) _lan = null;
        }
        // regtest es solo local: usa la DHT únicamente si se configuraron routers propios (pruebas).
        if (_config.Dht && !ProxyMode && (_network.Name != NetworkParams.Regtest.Name || _config.DhtRouters.Count > 0))
        {
            _dht = new MainlineDht(_config.DhtRouters.Count > 0 ? _config.DhtRouters : null, _config.AllowLoopbackPeers);
            if (_dht.Start(m => _log.Warn("dht", m)))
            {
                _dhtStatus = _dhtStatus with { Running = true };
                _ = Task.Run(() => DhtLoopAsync(_cts.Token));
            }
            else
            {
                _dht.Dispose();
                _dht = null;
            }
        }
        if (_config.Announce is { } announce && IsListening) _ = Task.Run(() => ResolveAnnounceAsync(announce));
        if (_config.Upnp && IsListening) _ = Task.Run(() => UpnpLoopAsync(_cts.Token));
        if (_relay is not null)
        {
            _log.Info("relay", IsListening
                ? "Servicio de relay activado: los nodos sin puertos abiertos podrán recibir conexiones a través de este nodo."
                : "El relay necesita aceptar conexiones entrantes (listen=1); queda desactivado.");
        }
        foreach (var address in GetLocalAddresses()) _log.Info("p2p", $"Dirección local: {address}");

        _ = Task.Run(() => OutboundLoopAsync(_cts.Token));
        _ = Task.Run(() => MaintenanceLoopAsync(_cts.Token));
    }

    private void AddConfigured(string text, string source)
    {
        if (!AddressSpec.TryParse(text, _network.DefaultPort, out var spec, out var error))
        {
            _log.Warn("p2p", $"Dirección {source} ignorada «{text}»: {error}");
            return;
        }
        if (spec!.IsLiteral)
        {
            foreach (var address in spec.ResolveAsync().GetAwaiter().GetResult()) AddManual(address, source);
            return;
        }
        _ = Task.Run(async () =>
        {
            var resolved = await ResolveAsync(spec).ConfigureAwait(false);
            if (resolved.Count == 0) _log.Warn("p2p", $"No se pudo resolver {spec.Host}. Se reintentará más tarde.");
            foreach (var address in resolved) AddManual(address, source);
        });
    }

    /// <summary>Con proxy, los nombres se resuelven a través de Tor: la consulta DNS no sale por el proveedor.</summary>
    private Task<List<NodeAddress>> ResolveAsync(AddressSpec spec) =>
        spec.ResolveAsync(_cts.Token, _proxy is { } proxy ? (host, token) => Socks5Client.ResolveAsync(proxy, host, token) : null);

    /// <summary>Abre la conexión TCP con un nodo: directa, o por el proxy SOCKS5 en modo privado.</summary>
    private async Task<TcpClient> OpenTcpAsync(PeerAddress endpoint, CancellationToken token)
    {
        if (_proxy is { } proxy) return await Socks5Client.ConnectAsync(proxy, endpoint.EndPoint, token).ConfigureAwait(false);
        var client = new TcpClient(endpoint.Ip.AddressFamily);
        try
        {
            await client.ConnectAsync(endpoint.EndPoint, token).ConfigureAwait(false);
            return client;
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    private void AddManual(NodeAddress address, string source)
    {
        if (address.NodeId == PeerId) return;
        _manualKeys[address.Key] = 0;
        _book.Add(address, source, manual: true);
    }

    private async Task ResolveAnnounceAsync(string text)
    {
        if (!AddressSpec.TryParse(text, ListeningPort, out var spec, out var error))
        {
            _log.Warn("p2p", $"announce inválido «{text}»: {error}");
            return;
        }
        var resolved = await ResolveAsync(spec!).ConfigureAwait(false);
        lock (_reachGate)
        {
            foreach (var a in resolved) _announced.Add(new NodeAddress(a.Endpoint, PeerId));
        }
        foreach (var a in resolved) _log.Info("p2p", $"Dirección anunciada (announce): {new NodeAddress(a.Endpoint, PeerId)}");
    }

    private void StartListener()
    {
        var port = _config.EffectivePort;
        try
        {
            var listener = new TcpListener(IPAddress.IPv6Any, port);
            listener.Server.DualMode = true;
            listener.Start();
            _listener = listener;
        }
        catch (SocketException)
        {
            try
            {
                var listener = new TcpListener(IPAddress.Any, port);
                listener.Start();
                _listener = listener;
            }
            catch (SocketException ex)
            {
                _log.Error("p2p", $"No se pudo escuchar en el puerto {port} ({ex.SocketErrorCode}). El nodo funcionará solo con conexiones salientes.");
                return;
            }
        }
        ListeningPort = ((IPEndPoint)_listener!.LocalEndpoint).Port;
        _log.Info("p2p", $"Escuchando conexiones P2P en el puerto TCP {ListeningPort}.");
        _ = Task.Run(() => AcceptLoopAsync(_cts.Token));
    }

    private void OnLanPeer(PeerAddress address)
    {
        if (_book.Add(address, "lan"))
        {
            _log.Info("lan", $"Nodo encontrado en la red local: {address}.");
        }
    }

    /// <summary>Agrega un par manual (multiaddr, IP:puerto o host:puerto) y trata de conectarse de inmediato.</summary>
    public async Task<bool> AddPeerAsync(string text)
    {
        if (!AddressSpec.TryParse(text, _network.DefaultPort, out var spec, out var error))
        {
            _log.Warn("p2p", $"Dirección inválida «{text}»: {error}");
            return false;
        }
        var addresses = await ResolveAsync(spec!).ConfigureAwait(false);
        if (addresses.Count == 0) return false;
        foreach (var address in addresses) AddManual(address, "manual");
        var normalized = text.Trim();
        if (!_config.AddNodes.Contains(normalized)) _config.AddNodes.Add(normalized);
        foreach (var address in addresses)
        {
            if (await TryConnectAsync(address).ConfigureAwait(false)) return true;
        }
        return false;
    }

    // =====================================================================
    // Conexiones
    // =====================================================================

    private async Task AcceptLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested && _listener is not null)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
            {
                if (token.IsCancellationRequested) return;
                continue;
            }

            var remote = PeerAddress.Normalize(((IPEndPoint)client.Client.RemoteEndPoint!).Address);
            var sameIp = _peers.Values.Count(p => p.Inbound && !p.IsRelayed && p.RemoteIp.Equals(remote));
            var perIpLimit = IPAddress.IsLoopback(remote) ? 64 : MaxPerIpInbound;
            if (_book.IsBanned(remote) || sameIp >= perIpLimit || Volatile.Read(ref _pendingInbound) >= MaxPendingInbound)
            {
                client.Dispose();
                continue;
            }
            Interlocked.Increment(ref _pendingInbound);
            _ = Task.Run(() => HandleIncomingAsync(client, token));
        }
    }

    /// <summary>
    /// Lee la primera trama de una conexión entrante (máx. 8 KiB, 10 s) y decide: par normal (HELLO),
    /// circuito de relay (CIRCUIT_CONNECT / CIRCUIT_ACCEPT) o prueba de alcanzabilidad (DIAL_BACK_PROBE).
    /// </summary>
    private async Task HandleIncomingAsync(TcpClient client, CancellationToken token)
    {
        Message first;
        Stream stream;
        try
        {
            stream = client.GetStream();
            first = await FrameIO.ReadMessageAsync(stream, _network, FirstFrameTimeout, token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is FrameException or FormatException or IOException or EndOfStreamException
                                       or SocketException or OperationCanceledException or ObjectDisposedException or InvalidOperationException)
        {
            client.Dispose();
            return;
        }
        finally
        {
            Interlocked.Decrement(ref _pendingInbound);
        }

        switch (first)
        {
            case HelloMessage:
                if (_peers.Values.Count(p => p.Inbound) >= _config.MaxInbound)
                {
                    client.Dispose();
                    return;
                }
                StartPeer(client, stream, inbound: true, dialed: null, expected: null, relayedVia: null, first);
                break;
            case CircuitConnectMessage connect when _relay is not null:
                await _relay.HandleConnectAsync(client, stream, connect, token).ConfigureAwait(false);
                break;
            case CircuitAcceptMessage accept when _relay is not null:
                await _relay.HandleAcceptAsync(client, stream, accept, token).ConfigureAwait(false);
                break;
            case CircuitConnectMessage:
                await FrameIO.WriteAsync(stream, _network, new CircuitStatusMessage(RelayStatus.NotRelay), token).ConfigureAwait(false);
                client.Dispose();
                break;
            case DialBackProbeMessage probe:
                if (_pendingProbes.TryGetValue(probe.Nonce, out var state)) state.Received = true;
                client.Dispose();
                break;
            default:
                client.Dispose();
                break;
        }
    }

    private bool IsConnectedTo(PeerId id) =>
        _peers.Values.Any(p => !p.IsClosed && (p.RemoteHello?.NodeId == id || p.ExpectedPeerId == id));

    private async Task<bool> TryConnectAsync(NodeAddress address)
    {
        if (address.NodeId == PeerId)
        {
            _book.Remove(address);
            return false;
        }
        if (address.NodeId is { } nodeId && (_book.IsBanned(nodeId) || IsConnectedTo(nodeId))) return IsConnectedTo(nodeId);
        if (!address.IsCircuit && _book.IsBanned(address.Endpoint.Ip)) return false;
        if (address.IsCircuit && address.PeerId == PeerId) return false;
        if (_peers.Values.Any(p => p.DialedAddress?.Key == address.Key || (!address.IsCircuit && p.ListenAddress?.Key == address.Key))) return true;
        if (!_connecting.TryAdd(address.Key, 0)) return false;
        _book.MarkAttempt(address);
        TcpClient? client = null;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(ProxyMode ? 45 : address.IsCircuit ? 20 : 5));
            client = await OpenTcpAsync(address.Endpoint, timeout.Token).ConfigureAwait(false);
            var stream = client.GetStream();
            if (address.IsCircuit)
            {
                await FrameIO.WriteAsync(stream, _network, new CircuitConnectMessage(address.CircuitTarget!), timeout.Token).ConfigureAwait(false);
                var reply = await FrameIO.ReadMessageAsync(stream, _network, TimeSpan.FromSeconds(15), timeout.Token).ConfigureAwait(false);
                if (reply is not CircuitStatusMessage { Status: RelayStatus.Ok } status)
                {
                    var reason = reply is CircuitStatusMessage s ? s.Status.ToString() : "respuesta inesperada";
                    if (_book.GetFailures(address) == 0) _log.Info("relay", $"El relay {address.Endpoint} no pudo abrir el circuito ({reason}).");
                    client.Dispose();
                    _book.MarkFailure(address);
                    return false;
                }
            }
            StartPeer(client, stream, inbound: false, dialed: address, expected: address.NodeId,
                relayedVia: address.IsCircuit ? address.PeerId : null, firstMessage: null,
                remoteEndPoint: ProxyMode ? address.Endpoint.EndPoint : null);
            return true;
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException or ObjectDisposedException
                                       or IOException or EndOfStreamException or FrameException or FormatException or Socks5Exception)
        {
            client?.Dispose();
            _book.MarkFailure(address);
            return false;
        }
        finally
        {
            _connecting.TryRemove(address.Key, out _);
        }
    }

    private void StartPeer(TcpClient client, Stream stream, bool inbound, NodeAddress? dialed, PeerId? expected, PeerId? relayedVia, Message? firstMessage,
        IPEndPoint? remoteEndPoint = null)
    {
        PeerConnection peer;
        try
        {
            peer = new PeerConnection(client, stream, _network, inbound, firstMessage, remoteEndPoint)
            {
                DialedAddress = dialed,
                ExpectedPeerId = expected,
                RelayedVia = relayedVia,
            };
        }
        catch (Exception ex) when (ex is SocketException or ObjectDisposedException or InvalidOperationException)
        {
            client.Dispose();
            return;
        }
        _peers[peer.Id] = peer;
        peer.Closed += OnPeerClosed;
        peer.Send(BuildHello(peer));
        _ = Task.Run(() => peer.RunAsync(HandleMessageAsync, _cts.Token));
        PeersChanged?.Invoke();
    }

    private NodeServices LocalServices =>
        (_relay is not null && IsListening ? NodeServices.Relay : NodeServices.None)
        | (Reachability == Reachability.Public ? NodeServices.Reachable : NodeServices.None);

    private HelloMessage BuildHello(PeerConnection peer)
    {
        var tip = _chain.Tip;
        var observedIp = peer.IsRelayed ? IPAddress.IPv6Any : peer.RemoteIp;
        var observedPort = peer.IsRelayed ? 0 : peer.RemoteEndPoint.Port;
        return new HelloMessage(Protocol.Version, _network.Magic, _chain.GenesisHash, tip.Height, tip.TotalWork,
            tip.Hash, ListeningPort, LocalServices, PeerId, peer.LocalChallenge, observedIp, observedPort,
            _config.UserAgent, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
    }

    private void OnPeerClosed(PeerConnection peer, string reason)
    {
        _peers.TryRemove(peer.Id, out _);
        if (peer.Misbehavior >= 100)
        {
            // La identidad solo se banea si fue verificada; la IP no se banea si el par llegó por un relay
            // (sería castigar al relay por lo que hizo otro).
            if (peer.RemotePeerId is { } id) _book.Ban(id, BanDuration);
            if (!peer.IsRelayed && !(IPAddress.IsLoopback(peer.RemoteIp) && _config.AllowLoopbackPeers))
            {
                _book.Ban(peer.RemoteIp, BanDuration);
            }
        }
        if (!peer.Inbound && !peer.HandshakeComplete && peer.DialedAddress is not null)
        {
            _book.MarkFailure(peer.DialedAddress);
        }
        _relay?.OnPeerClosed(peer);
        if (_reservations.TryRemove(peer.Id, out var reservation))
        {
            _log.Warn("relay", $"Se perdió la reserva en el relay {reservation.RelayEndpoint}. Buscando otro relay…");
        }
        _reservationRequests.TryRemove(peer.Id, out _);
        if (peer.PendingDialBack is { } nonce) _pendingProbes.TryRemove(nonce, out _);
        lock (_syncGate)
        {
            if (_syncPeer == peer) _syncPeer = null;
        }
        if (peer.HandshakeComplete)
        {
            _log.Info("p2p", $"Desconectado de {peer.RemotePeerId!.Short} ({peer.DisplayAddress}): {reason}");
        }
        PeersChanged?.Invoke();
    }

    private void Misbehave(PeerConnection peer, int score, string reason)
    {
        peer.Misbehavior += score;
        _log.Warn("p2p", $"Par {peer.DisplayAddress} penalizado (+{score}): {reason}");
        if (peer.Misbehavior >= 100) peer.Close($"Expulsado por mal comportamiento: {reason}");
    }

    // =====================================================================
    // Mensajes
    // =====================================================================

    private Task HandleMessageAsync(PeerConnection peer, Message message)
    {
        if (!peer.HandshakeComplete && message is not (HelloMessage or HelloAckMessage))
        {
            Misbehave(peer, 25, "mensaje recibido antes de completar el handshake");
            return Task.CompletedTask;
        }

        switch (message)
        {
            case HelloMessage hello:
                OnHello(peer, hello);
                break;
            case HelloAckMessage ack:
                OnHelloAck(peer, ack);
                break;
            case PingMessage ping:
                peer.Send(new PongMessage(ping.Nonce));
                break;
            case PongMessage pong:
                if (pong.Nonce != 0 && pong.Nonce == peer.PendingPingNonce)
                {
                    peer.PingMs = (Environment.TickCount64 - peer.PingSentTicks);
                    peer.PendingPingNonce = 0;
                }
                break;
            case GetPeersMessage:
                peer.Send(new PeersMessage(BuildPeersReply(peer)));
                break;
            case PeersMessage peers:
                OnPeers(peer, peers);
                break;
            case GetBlocksMessage getBlocks:
                peer.Send(new InvMessage(InvType.Block, _chain.GetHashesAfterLocator(getBlocks.Locator, getBlocks.Stop, Protocol.MaxInventory)));
                break;
            case InvMessage inv:
                OnInventory(peer, inv);
                break;
            case GetDataMessage getData:
                OnGetData(peer, getData);
                break;
            case NotFoundMessage notFound:
                lock (peer.BlocksInFlight)
                {
                    foreach (var h in notFound.Hashes) peer.BlocksInFlight.Remove(h);
                }
                foreach (var h in notFound.Hashes) _txRequested.TryRemove(h, out _);
                break;
            case BlockMessage block:
                OnBlock(peer, block);
                break;
            case TxMessage tx:
                OnTransaction(peer, tx);
                break;
            case StemTxMessage stem:
                OnStemTransaction(peer, stem);
                break;
            case RelayReserveMessage:
                OnRelayReserve(peer);
                break;
            case RelayReservationMessage reservation:
                OnRelayReservation(peer, reservation);
                break;
            case CircuitIncomingMessage incoming:
                OnCircuitIncoming(peer, incoming);
                break;
            case DialBackRequestMessage request:
                OnDialBackRequest(peer, request);
                break;
            case DialBackResultMessage result:
                OnDialBackResult(peer, result);
                break;
            default:
                Misbehave(peer, 10, $"mensaje {message.Type} fuera de contexto");
                break;
        }
        return Task.CompletedTask;
    }

    private void OnHello(PeerConnection peer, HelloMessage hello)
    {
        if (peer.RemoteHello is not null)
        {
            Misbehave(peer, 25, "HELLO duplicado");
            return;
        }
        if (hello.NodeId == PeerId)
        {
            if (peer.DialedAddress is not null)
            {
                _selfKeys[peer.DialedAddress.Key] = 0;
                _book.Remove(peer.DialedAddress);
            }
            peer.Close("Conexión con uno mismo.");
            return;
        }
        if (hello.NetworkMagic != _network.Magic || hello.GenesisHash != _chain.GenesisHash)
        {
            if (peer.DialedAddress is not null) _book.Remove(peer.DialedAddress);
            peer.Close("El par pertenece a otra red SHENT.");
            return;
        }
        if (hello.ProtocolVersion < Protocol.MinVersion)
        {
            peer.Close("Versión de protocolo demasiado antigua: el otro nodo tiene que actualizar SHENT.");
            return;
        }
        if (peer.ExpectedPeerId is { } expected && expected != hello.NodeId)
        {
            _log.Warn("p2p", $"{peer.DisplayAddress} respondió con el Peer ID {hello.NodeId}, pero se esperaba {expected}. " +
                             "Revisá la dirección configurada; la conexión se rechaza.");
            peer.Close("Peer ID distinto del esperado.");
            return;
        }
        if (_book.IsBanned(hello.NodeId))
        {
            peer.Close("Nodo baneado.");
            return;
        }
        var skew = Math.Abs(hello.Timestamp - DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        if (skew > 600)
        {
            _log.Warn("p2p", $"El reloj de {peer.DisplayAddress} difiere {skew / 60} min del tuyo. Verificá la hora del sistema.");
        }
        peer.RemoteHello = hello;
        // Altura y trabajo anunciados: solo orientan la elección del par para sincronizar. Nunca se usan
        // para aceptar bloques; si resultan falsos, se corrigen al terminar la sincronización.
        peer.BestHeight = hello.BestHeight;
        peer.BestWork = hello.TotalWork;
        peer.Send(new HelloAckMessage(_identity.SignHandshake(_network.Magic, hello.Challenge, hello.NodeId)));
    }

    private void OnHelloAck(PeerConnection peer, HelloAckMessage ack)
    {
        if (peer.AckReceived)
        {
            Misbehave(peer, 10, "HELLO_ACK duplicado");
            return;
        }
        if (peer.RemoteHello is null)
        {
            Misbehave(peer, 50, "HELLO_ACK antes de HELLO");
            return;
        }
        if (!NodeIdentity.VerifyHandshake(_network.Magic, peer.LocalChallenge, peer.RemoteHello.NodeId, PeerId, ack.Signature))
        {
            Misbehave(peer, 100, "firma de identidad inválida en el handshake");
            return;
        }
        peer.AckReceived = true;
        OnHandshakeComplete(peer);
    }

    /// <summary>
    /// Ante dos conexiones con el mismo nodo, ambos lados conservan la misma: primero la directa sobre la
    /// de relay, y luego la de menor desafío del lado que marcó (valor que ambos conocen).
    /// </summary>
    private static PeerConnection Preferred(PeerConnection a, PeerConnection b)
    {
        if (a.IsRelayed != b.IsRelayed) return a.IsRelayed ? b : a;
        static ReadOnlySpan<byte> DialerChallenge(PeerConnection c) => c.Inbound ? c.RemoteHello!.Challenge.Span : c.LocalChallenge.Span;
        return DialerChallenge(a).SequenceCompareTo(DialerChallenge(b)) <= 0 ? a : b;
    }

    private void OnHandshakeComplete(PeerConnection peer)
    {
        var id = peer.RemotePeerId!;
        foreach (var other in _peers.Values)
        {
            if (other == peer || other.IsClosed || other.RemotePeerId != id) continue;
            var keep = Preferred(peer, other);
            var drop = keep == peer ? other : peer;
            drop.Close("Conexión duplicada con el mismo nodo.");
            if (drop == peer) return;
        }

        _log.Success("p2p", $"Conectado a {id.Short} en {peer.DisplayAddress} ({(peer.Inbound ? "entrante" : "saliente")}, " +
                            $"{peer.RemoteHello!.UserAgent}, altura anunciada {peer.BestHeight}).");
        if (peer.DialedAddress is not null)
        {
            _book.MarkSuccess(peer.DialedAddress, id);
        }
        else if (peer.ListenAddress is { } listen && AddressBook.IsAcceptable(listen, _config.AllowLoopbackPeers)
                 && (peer.RemoteHello.Services.HasFlag(NodeServices.Reachable) || AddressBook.IsPrivate(listen.Ip)))
        {
            var address = new NodeAddress(listen, id);
            if (_book.Add(address, "inbound")) ForwardAddress(address, except: peer);
        }
        peer.Send(new GetPeersMessage());
        var pending = _mempool.GetAll().Take(Protocol.MaxInventory).Select(t => t.Id).ToList();
        if (pending.Count > 0)
        {
            foreach (var txId in pending) peer.MarkKnown(txId);
            peer.Send(new InvMessage(InvType.Tx, pending));
        }
        MaybeStartSync(peer);
        if (Reachability == Reachability.Unknown) MaybeRequestDialBack(peer);
        PeersChanged?.Invoke();
    }

    private List<NodeAddress> BuildPeersReply(PeerConnection requester)
    {
        var includePrivate = AddressBook.IsPrivate(requester.RemoteIp) && !requester.IsRelayed;
        var list = new List<NodeAddress>(GetOwnAddresses());
        if (_relay is not null && PublicAddress is { } relayPublic)
        {
            foreach (var reserved in _relay.ReservedPeers().Take(20))
            {
                if (reserved != requester.RemotePeerId) list.Add(new NodeAddress(relayPublic, PeerId, reserved));
            }
        }
        list.AddRange(_book.GetShareable(Protocol.MaxPeersPerMessage, includePrivate)
            .Where(a => a.NodeId != requester.RemotePeerId));
        return list.Where(a => includePrivate || !AddressBook.IsPrivate(a.Endpoint.Ip))
            .DistinctBy(a => a.Key)
            .Take(Protocol.MaxPeersPerMessage)
            .ToList();
    }

    /// <summary>Direcciones propias que vale la pena anunciar: pública verificada, announce y circuitos de relay.</summary>
    private List<NodeAddress> GetOwnAddresses()
    {
        var list = new List<NodeAddress>();
        lock (_reachGate)
        {
            list.AddRange(_announced);
            if (Reachability == Reachability.Public && PublicAddress is { } pub) list.Add(new NodeAddress(pub, PeerId));
        }
        var now = DateTimeOffset.UtcNow;
        foreach (var r in _reservations.Values)
        {
            if (r.Relay.IsClosed || r.Expires <= now || r.Relay.RemotePeerId is null) continue;
            list.Add(new NodeAddress(r.RelayEndpoint, r.Relay.RemotePeerId, PeerId));
        }
        return list.DistinctBy(a => a.Key).ToList();
    }

    private void OnPeers(PeerConnection peer, PeersMessage message)
    {
        foreach (var address in message.Addresses)
        {
            if (address.NodeId == PeerId || (address.IsCircuit && address.PeerId == PeerId)) continue;
            if (!AddressBook.IsAcceptable(address.Endpoint, _config.AllowLoopbackPeers)) continue;
            if (AddressBook.IsPrivate(address.Endpoint.Ip) && !AddressBook.IsPrivate(peer.RemoteIp)) continue;
            // Las autoanuncios nuevos (el par anuncia su propia dirección) se reenvían a otros dos pares;
            // como solo se reenvía lo que es nuevo para la libreta, la difusión se corta sola.
            if (_book.Add(address, "peer") && address.NodeId == peer.RemotePeerId) ForwardAddress(address, except: peer);
        }
    }

    private void ForwardAddress(NodeAddress address, PeerConnection except)
    {
        var targets = _peers.Values
            .Where(p => p != except && p.HandshakeComplete && !p.IsClosed && p.RemotePeerId != address.NodeId
                        && (!AddressBook.IsPrivate(address.Endpoint.Ip) || AddressBook.IsPrivate(p.RemoteIp)))
            .OrderBy(_ => RandomNumberGenerator.GetInt32(int.MaxValue))
            .Take(2);
        foreach (var target in targets) target.Send(new PeersMessage([address]));
    }

    private void AnnounceOwnAddresses()
    {
        var own = GetOwnAddresses();
        if (own.Count == 0) return;
        foreach (var peer in _peers.Values)
        {
            if (peer.HandshakeComplete) peer.Send(new PeersMessage(own.Where(a => a.CircuitTarget is null || a.PeerId != peer.RemotePeerId).ToList()));
        }
    }

    private bool IsInFlightAnywhere(Hash256 hash)
    {
        foreach (var p in _peers.Values)
        {
            lock (p.BlocksInFlight)
            {
                if (p.BlocksInFlight.Contains(hash)) return true;
            }
        }
        return false;
    }

    private bool IsRecentlyRejected(Hash256 id)
    {
        lock (_rejectGate) return _recentRejects.Contains(id);
    }

    private void RememberRejected(Hash256 id)
    {
        lock (_rejectGate)
        {
            if (_recentRejects.Count >= MaxRecentRejects) _recentRejects.Clear();
            _recentRejects.Add(id);
        }
    }

    private void OnInventory(PeerConnection peer, InvMessage inv)
    {
        foreach (var h in inv.Hashes) peer.MarkKnown(h);

        if (inv.Kind == InvType.Tx)
        {
            var now = Environment.TickCount64;
            var wanted = new List<Hash256>();
            foreach (var h in inv.Hashes)
            {
                // Otro nodo ya la está difundiendo: la fase privada terminó, se difunde también desde acá.
                if (_mempool.GetPrivate(h) is { } stemmed)
                {
                    Fluff(stemmed, except: peer);
                    continue;
                }
                if (_mempool.Contains(h) || IsRecentlyRejected(h)) continue;
                // Una transacción anunciada por varios pares se pide a uno solo (salvo que no responda).
                if (_txRequested.TryGetValue(h, out var requestedAt) && now - requestedAt < TxRequestTimeout.TotalMilliseconds) continue;
                _txRequested[h] = now;
                wanted.Add(h);
            }
            if (wanted.Count > 0) peer.Send(new GetDataMessage(InvType.Tx, wanted));
            return;
        }

        if (peer.Syncing) peer.LastSyncActivity = DateTimeOffset.UtcNow;
        List<Hash256> unknown;
        lock (_orphanGate)
        {
            unknown = inv.Hashes.Where(h => !_chain.HasBlock(h) && !_orphans.ContainsKey(h) && !IsInFlightAnywhere(h)).ToList();
        }

        if (unknown.Count == 0)
        {
            if (!peer.Syncing) return;
            if (inv.Hashes.Count >= Protocol.MaxInventory)
            {
                var locator = new List<Hash256> { inv.Hashes[^1] };
                locator.AddRange(_chain.GetLocator().Take(Protocol.MaxLocator - 1));
                peer.Send(new GetBlocksMessage(locator, Hash256.Zero));
            }
            else
            {
                FinishSync(peer);
            }
            return;
        }

        if (inv.Hashes.Count == 1 && !peer.Syncing)
        {
            peer.BestHeight = Math.Max(peer.BestHeight, _chain.Height + 1);
        }
        RequestBlocks(peer, unknown);
    }

    private void RequestBlocks(PeerConnection peer, List<Hash256> hashes)
    {
        for (var i = 0; i < hashes.Count; i += BlocksPerRequest)
        {
            var chunk = hashes.Skip(i).Take(BlocksPerRequest).ToList();
            lock (peer.BlocksInFlight)
            {
                foreach (var h in chunk) peer.BlocksInFlight.Add(h);
            }
            peer.Send(new GetDataMessage(InvType.Block, chunk));
        }
    }

    private void OnGetData(PeerConnection peer, GetDataMessage request)
    {
        var missing = new List<Hash256>();
        foreach (var hash in request.Hashes)
        {
            if (request.Kind == InvType.Block)
            {
                var block = _chain.GetBlock(hash);
                if (block is null) missing.Add(hash);
                else peer.Send(new BlockMessage(block.Serialize()));
            }
            else
            {
                var tx = _mempool.Get(hash);
                if (tx is null) missing.Add(hash);
                else peer.Send(new TxMessage(tx.Serialize()));
            }
        }
        if (missing.Count > 0) peer.Send(new NotFoundMessage(request.Kind, missing));
    }

    private void OnBlock(PeerConnection peer, BlockMessage message)
    {
        Block block;
        try
        {
            block = Block.Deserialize(message.Payload, _network.MaxBlockBytes);
        }
        catch (FormatException ex)
        {
            Misbehave(peer, 100, $"bloque malformado ({ex.Message})");
            return;
        }

        var hash = block.Hash;
        peer.MarkKnown(hash);
        lock (peer.BlocksInFlight) peer.BlocksInFlight.Remove(hash);
        peer.LastSyncActivity = DateTimeOffset.UtcNow;

        ProcessWithOrphans(peer, block);

        // Solo cuando el bloque fue validado y quedó en la cadena se sube la estimación de lo que tiene el par.
        var tip = _chain.Tip;
        if (_chain.HasBlock(hash))
        {
            peer.BestHeight = Math.Max(peer.BestHeight, block.Header.Height);
            if (tip.Hash == hash) peer.BestWork = BigInteger.Max(peer.BestWork, tip.TotalWork);
        }

        bool inFlightEmpty;
        lock (peer.BlocksInFlight) inFlightEmpty = peer.BlocksInFlight.Count == 0;
        if (peer.Syncing && inFlightEmpty)
        {
            peer.Send(new GetBlocksMessage(_chain.GetLocator(), Hash256.Zero));
        }
    }

    private void ProcessWithOrphans(PeerConnection peer, Block first)
    {
        var queue = new Queue<Block>();
        queue.Enqueue(first);
        while (queue.Count > 0)
        {
            var block = queue.Dequeue();
            var result = _chain.ProcessBlock(block);
            switch (result.Status)
            {
                case BlockAcceptStatus.Orphan:
                    AddOrphan(block);
                    StartSyncWith(peer);
                    break;
                case BlockAcceptStatus.Invalid:
                    if (result.Punishable) Misbehave(peer, 100, $"bloque {block.Header.Height} inválido: {result.Reason}");
                    else _log.Warn("chain", $"Bloque {block.Header.Height} rechazado: {result.Reason}");
                    break;
                default:
                    foreach (var child in TakeOrphanChildren(block.Hash)) queue.Enqueue(child);
                    break;
            }
        }
    }

    private void AddOrphan(Block block)
    {
        lock (_orphanGate)
        {
            if (_orphans.Count >= MaxOrphans)
            {
                var victim = _orphans.Keys.ElementAt(RandomNumberGenerator.GetInt32(_orphans.Count));
                _orphans.Remove(victim);
            }
            _orphans[block.Hash] = block;
        }
    }

    private List<Block> TakeOrphanChildren(Hash256 parent)
    {
        lock (_orphanGate)
        {
            var children = _orphans.Values.Where(b => b.Header.PrevHash == parent).ToList();
            foreach (var c in children) _orphans.Remove(c.Hash);
            return children;
        }
    }

    private void OnTransaction(PeerConnection peer, TxMessage message)
    {
        Transaction tx;
        try
        {
            tx = Transaction.Deserialize(message.Payload);
        }
        catch (FormatException ex)
        {
            Misbehave(peer, 50, $"transacción malformada ({ex.Message})");
            return;
        }
        peer.MarkKnown(tx.Id);
        _txRequested.TryRemove(tx.Id, out _);
        if (_mempool.Contains(tx.Id))
        {
            if (_mempool.IsPrivate(tx.Id)) Fluff(tx, except: peer);
            return;
        }
        var result = _mempool.TryAdd(tx);
        if (result.Accepted)
        {
            RelayTransaction(tx, except: peer);
        }
        else
        {
            RememberRejected(tx.Id);
            if (result.Punishable) Misbehave(peer, 20, $"transacción inválida: {result.Error}");
        }
    }

    // =====================================================================
    // Dandelion++ (privacidad del origen de las transacciones)
    //
    // Fase de tallo: la transacción va de a un nodo por vez por un camino aleatorio; cada nodo la valida
    // completa (firma, saldo, nonce) antes de reenviarla, así que nadie puede inyectar basura por el tallo.
    // En cada época de 10 minutos un nodo es "difusor" con probabilidad 0,1: si lo es, difunde la
    // transacción a todos (fase de flor). Quien la ve difundida no sabe en qué punto del tallo empezó.
    // Si el tallo se corta (un par que se la guarda), el temporizador de embargo la difunde igual.
    // =====================================================================

    /// <summary>Envía una transacción creada en esta PC: por el tallo si hay pares compatibles, si no la difunde.</summary>
    public MempoolResult SubmitLocalTransaction(Transaction tx)
    {
        var stem = _config.Dandelion ? StemTarget(Guid.Empty, exclude: null) : null;
        var result = _mempool.TryAdd(tx, isPrivate: stem is not null);
        if (!result.Accepted) return result;
        if (stem is null)
        {
            Fluff(tx, except: null);
        }
        else
        {
            SendStem(stem, tx, own: true);
        }
        return result;
    }

    private void OnStemTransaction(PeerConnection peer, StemTxMessage message)
    {
        Transaction tx;
        try
        {
            tx = Transaction.Deserialize(message.Payload);
        }
        catch (FormatException ex)
        {
            Misbehave(peer, 50, $"transacción malformada ({ex.Message})");
            return;
        }
        if (_mempool.Contains(tx.Id) || IsRecentlyRejected(tx.Id)) return;

        var stem = _config.Dandelion && !IsDiffuserEpoch() ? StemTarget(peer.Id, exclude: peer) : null;
        var result = _mempool.TryAdd(tx, isPrivate: stem is not null);
        if (!result.Accepted)
        {
            RememberRejected(tx.Id);
            if (result.Punishable) Misbehave(peer, 20, $"transacción inválida: {result.Error}");
            return;
        }
        // Al difundir también se avisa a quien la mandó por el tallo: ya sabía que este nodo la tenía, y así
        // deja de guardarla en privado sin esperar su embargo.
        if (stem is null) Fluff(tx, except: null);
        else SendStem(stem, tx, own: false);
    }

    private void SendStem(PeerConnection target, Transaction tx, bool own)
    {
        // Embargo: el origen espera más que los nodos intermedios, para no ser el primero en difundir lo propio.
        var embargo = (own ? 45_000 : 15_000) + ExponentialMs(own ? 15_000 : 10_000);
        _embargo[tx.Id] = Environment.TickCount64 + embargo;
        target.MarkKnown(tx.Id);
        target.Send(new StemTxMessage(tx.Serialize()));
    }

    private static long ExponentialMs(double mean)
    {
        var u = (RandomNumberGenerator.GetInt32(1, int.MaxValue)) / (double)int.MaxValue;
        return (long)Math.Min(mean * 6, -mean * Math.Log(u));
    }

    /// <summary>Fase de flor: la transacción pasa a pública y se anuncia a todos los pares.</summary>
    private void Fluff(Transaction tx, PeerConnection? except)
    {
        _mempool.MakePublic(tx.Id);
        _embargo.TryRemove(tx.Id, out _);
        RelayTransaction(tx, except);
    }

    private bool IsDiffuserEpoch()
    {
        lock (_dandelionGate)
        {
            EnsureEpochLocked(DateTimeOffset.UtcNow);
            return _diffuser;
        }
    }

    private void EnsureEpochLocked(DateTimeOffset now)
    {
        if (now < _epochEnd) return;
        _epochEnd = now + DandelionEpoch;
        _diffuser = RandomNumberGenerator.GetInt32(1_000_000) < DiffuserProbability * 1_000_000;
        _stemPeers.Clear();
        _stemRoutes.Clear();
    }

    /// <summary>
    /// Par al que reenviar por el tallo lo que llega de <paramref name="source"/> (Guid.Empty = transacciones propias).
    /// Cada origen se asigna a un mismo par durante toda la época, como pide Dandelion++, para que un atacante
    /// no pueda sondear el grafo reenviando varias veces. Se prefieren conexiones salientes: son las que
    /// este nodo eligió y un atacante no puede imponer.
    /// </summary>
    private PeerConnection? StemTarget(Guid source, PeerConnection? exclude)
    {
        lock (_dandelionGate)
        {
            EnsureEpochLocked(DateTimeOffset.UtcNow);
            _stemPeers.RemoveAll(p => p.IsClosed);
            if (_stemPeers.Count < StemPeerCount)
            {
                var eligible = _peers.Values
                    .Where(p => p.HandshakeComplete && !p.IsClosed && p.SupportsDandelion && !_stemPeers.Contains(p))
                    .ToList();
                var preferred = eligible.Where(p => !p.Inbound).ToList();
                var pool = preferred.Count > 0 ? preferred : eligible;
                foreach (var p in pool.OrderBy(_ => RandomNumberGenerator.GetInt32(int.MaxValue)).Take(StemPeerCount - _stemPeers.Count))
                {
                    _stemPeers.Add(p);
                }
            }
            if (_stemRoutes.TryGetValue(source, out var route) && !route.IsClosed && route != exclude && _stemPeers.Contains(route))
            {
                return route;
            }
            var choices = _stemPeers.Where(p => p != exclude).ToList();
            if (choices.Count == 0) return null;
            var chosen = choices[RandomNumberGenerator.GetInt32(choices.Count)];
            _stemRoutes[source] = chosen;
            return chosen;
        }
    }

    private void ExpireEmbargoes()
    {
        var tick = Environment.TickCount64;
        foreach (var id in _mempool.GetPrivateIds())
        {
            if (!_embargo.ContainsKey(id)) _embargo[id] = tick + 15_000;
        }
        foreach (var (id, deadline) in _embargo)
        {
            if (tick < deadline) continue;
            _embargo.TryRemove(id, out _);
            if (_mempool.GetPrivate(id) is { } tx)
            {
                _log.Info("p2p", $"La transacción {id.ToShortString()} no apareció difundida a tiempo: se difunde desde este nodo (embargo de Dandelion++).");
                Fluff(tx, except: null);
            }
        }
    }

    // =====================================================================
    // Relay (cliente y servidor) y alcanzabilidad
    // =====================================================================

    private void OnRelayReserve(PeerConnection peer)
    {
        if (_relay is null || !IsListening)
        {
            peer.Send(new RelayReservationMessage(RelayStatus.NotRelay, 0));
            return;
        }
        var status = _relay.Reserve(peer, out var expires);
        peer.Send(new RelayReservationMessage(status, expires));
    }

    private void OnRelayReservation(PeerConnection peer, RelayReservationMessage message)
    {
        if (!_reservationRequests.TryRemove(peer.Id, out _))
        {
            Misbehave(peer, 5, "reserva de relay no solicitada");
            return;
        }
        if (message.Status != RelayStatus.Ok)
        {
            peer.RelayRetryAfter = DateTimeOffset.UtcNow.AddMinutes(10);
            _reservations.TryRemove(peer.Id, out _);
            _log.Info("relay", $"{peer.DisplayAddress} rechazó la reserva de relay ({message.Status}).");
            return;
        }
        var endpoint = peer.DialedAddress is { IsCircuit: false } dialed ? dialed.Endpoint : peer.ListenAddress;
        if (endpoint is null || peer.IsRelayed) return;
        var expires = DateTimeOffset.FromUnixTimeSeconds(Math.Min(message.ExpiresAt, DateTimeOffset.UtcNow.AddHours(2).ToUnixTimeSeconds()));
        var isNew = !_reservations.ContainsKey(peer.Id);
        _reservations[peer.Id] = new ClientReservation(peer, endpoint, expires);
        if (isNew)
        {
            var circuit = new NodeAddress(endpoint, peer.RemotePeerId, PeerId);
            _log.Success("relay", $"Reserva obtenida en el relay {endpoint}. Otros nodos pueden conectarse a esta PC con: {circuit}");
            AnnounceOwnAddresses();
        }
    }

    private void OnCircuitIncoming(PeerConnection relay, CircuitIncomingMessage message)
    {
        if (!_reservations.TryGetValue(relay.Id, out var reservation))
        {
            Misbehave(relay, 10, "circuito entrante sin reserva");
            return;
        }
        if (_peers.Values.Count(p => p.Inbound) >= _config.MaxInbound) return;
        _ = Task.Run(() => AcceptCircuitAsync(reservation, message.CircuitId));
    }

    private async Task AcceptCircuitAsync(ClientReservation reservation, Hash256 circuitId)
    {
        TcpClient? client = null;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(ProxyMode ? 45 : 15));
            client = await OpenTcpAsync(reservation.RelayEndpoint, timeout.Token).ConfigureAwait(false);
            var stream = client.GetStream();
            await FrameIO.WriteAsync(stream, _network, new CircuitAcceptMessage(circuitId), timeout.Token).ConfigureAwait(false);
            var reply = await FrameIO.ReadMessageAsync(stream, _network, TimeSpan.FromSeconds(10), timeout.Token).ConfigureAwait(false);
            if (reply is not CircuitStatusMessage { Status: RelayStatus.Ok })
            {
                client.Dispose();
                return;
            }
            StartPeer(client, stream, inbound: true, dialed: null, expected: null, relayedVia: reservation.Relay.RemotePeerId, firstMessage: null,
                remoteEndPoint: ProxyMode ? reservation.RelayEndpoint.EndPoint : null);
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException or ObjectDisposedException
                                       or IOException or EndOfStreamException or FrameException or FormatException or Socks5Exception)
        {
            client?.Dispose();
        }
    }

    private void MaintainRelayReservations(DateTimeOffset now)
    {
        if (!_config.RelayClient || Reachability != Reachability.Private) return;
        foreach (var (id, r) in _reservations)
        {
            if (r.Relay.IsClosed || r.Expires <= now) _reservations.TryRemove(id, out _);
        }
        foreach (var (id, requested) in _reservationRequests)
        {
            if (now - requested > TimeSpan.FromSeconds(30)) _reservationRequests.TryRemove(id, out _);
        }

        // Renovar reservas que vencen en menos de 30 minutos.
        foreach (var r in _reservations.Values)
        {
            if (r.Expires - now < TimeSpan.FromMinutes(30) && _reservationRequests.TryAdd(r.Relay.Id, now))
            {
                r.Relay.Send(new RelayReserveMessage());
            }
        }

        var missing = MaxRelayReservations - _reservations.Count - _reservationRequests.Count(k => !_reservations.ContainsKey(k.Key));
        if (missing <= 0) return;
        var candidates = _peers.Values
            .Where(p => p.HandshakeComplete && !p.IsClosed && !p.IsRelayed
                        && p.RemoteHello!.Services.HasFlag(NodeServices.Relay)
                        && !_reservations.ContainsKey(p.Id) && !_reservationRequests.ContainsKey(p.Id)
                        && now >= p.RelayRetryAfter
                        && AddressBook.IsAcceptable(new PeerAddress(p.RemoteIp, p.RemoteEndPoint.Port), _config.AllowLoopbackPeers))
            .OrderBy(p => p.PingMs ?? double.MaxValue)
            .Take(missing);
        foreach (var relay in candidates)
        {
            _reservationRequests[relay.Id] = now;
            relay.Send(new RelayReserveMessage());
        }
    }

    private bool CanAskDialBack(PeerConnection peer) =>
        IsListening && !peer.IsRelayed && peer.HandshakeComplete && peer.PendingDialBack is null
        && (!AddressBook.IsPrivate(peer.RemoteIp) || _config.AllowLoopbackPeers && IPAddress.IsLoopback(peer.RemoteIp));

    private void MaybeRequestDialBack(PeerConnection peer)
    {
        if (!CanAskDialBack(peer)) return;
        lock (_reachGate)
        {
            if (_roundRequested >= 3) return;
            _roundRequested++;
        }
        var nonce = new Hash256(RandomNumberGenerator.GetBytes(Hash256.Size));
        _pendingProbes[nonce] = new ProbeState(peer.Id);
        peer.PendingDialBack = nonce;
        peer.Send(new DialBackRequestMessage(nonce, ListeningPort));
    }

    private void StartDialBackRound(DateTimeOffset now)
    {
        lock (_reachGate)
        {
            _roundRequested = 0;
            _roundFailures = 0;
            _roundSuccesses = 0;
            _nextDialBackRound = now + (Reachability == Reachability.Public ? TimeSpan.FromMinutes(30) : TimeSpan.FromMinutes(10));
        }
        foreach (var peer in _peers.Values.Where(CanAskDialBack).OrderBy(_ => RandomNumberGenerator.GetInt32(int.MaxValue)).Take(3))
        {
            MaybeRequestDialBack(peer);
        }
    }

    /// <summary>
    /// Otro nodo pide que verifiquemos si su puerto es accesible. Solo se marca la IP desde la que nos habla
    /// (nunca una dirección arbitraria) y con límite de frecuencia, para no servir de amplificador.
    /// </summary>
    private void OnDialBackRequest(PeerConnection peer, DialBackRequestMessage request)
    {
        var now = DateTimeOffset.UtcNow;
        // En modo privado no se marca a nadie directamente: la conexión de prueba revelaría la IP real.
        if (ProxyMode || peer.IsRelayed || request.Port <= 0 || now - peer.LastDialBackServed < DialBackServeInterval)
        {
            peer.Send(new DialBackResultMessage(request.Nonce, false));
            return;
        }
        if (Interlocked.Increment(ref _dialBacksInFlight) > MaxConcurrentDialBacks)
        {
            Interlocked.Decrement(ref _dialBacksInFlight);
            peer.Send(new DialBackResultMessage(request.Nonce, false));
            return;
        }
        peer.LastDialBackServed = now;
        var target = new IPEndPoint(peer.RemoteIp, request.Port);
        _ = Task.Run(async () =>
        {
            var success = false;
            using var client = new TcpClient(target.AddressFamily);
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
                timeout.CancelAfter(TimeSpan.FromSeconds(5));
                await client.ConnectAsync(target, timeout.Token).ConfigureAwait(false);
                await FrameIO.WriteAsync(client.GetStream(), _network, new DialBackProbeMessage(request.Nonce), timeout.Token).ConfigureAwait(false);
                await Task.Delay(200, timeout.Token).ConfigureAwait(false);
                success = true;
            }
            catch (Exception ex) when (ex is SocketException or OperationCanceledException or IOException or ObjectDisposedException)
            {
            }
            finally
            {
                Interlocked.Decrement(ref _dialBacksInFlight);
            }
            peer.Send(new DialBackResultMessage(request.Nonce, success));
        });
    }

    private void OnDialBackResult(PeerConnection peer, DialBackResultMessage result)
    {
        if (peer.PendingDialBack != result.Nonce)
        {
            Misbehave(peer, 10, "resultado de dial-back no solicitado");
            return;
        }
        peer.PendingDialBack = null;
        var observed = peer.RemoteHello!.ObservedIp;
        _ = Task.Run(async () =>
        {
            // El par afirma que pudo conectarse; solo se cree si la prueba realmente llegó a nuestro puerto.
            if (result.Success && _pendingProbes.TryGetValue(result.Nonce, out var pending) && !pending.Received)
            {
                await Task.Delay(1000).ConfigureAwait(false);
            }
            var received = _pendingProbes.TryRemove(result.Nonce, out var state) && state.Received;
            if (result.Success && received && !observed.Equals(IPAddress.IPv6Any))
            {
                SetPublic(new PeerAddress(observed, ListeningPort));
                return;
            }
            bool becamePrivate;
            lock (_reachGate)
            {
                _roundFailures++;
                becamePrivate = _roundSuccesses == 0 && _roundFailures >= Math.Min(2, Math.Max(1, _roundRequested)) && Reachability != Reachability.Private;
            }
            if (becamePrivate) SetPrivate();
        });
    }

    private void SetPublic(PeerAddress address)
    {
        bool changed;
        lock (_reachGate)
        {
            _roundSuccesses++;
            changed = Reachability != Reachability.Public || PublicAddress != address;
            Reachability = Reachability.Public;
            PublicAddress = address;
        }
        if (!changed) return;
        _log.Success("p2p", $"Este nodo es accesible desde Internet. Compartí esta dirección: {new NodeAddress(address, PeerId)}");
        if (_upnp?.ExternalIp is { } upnpIp && !AddressBook.IsPrivate(upnpIp) && !upnpIp.Equals(address.Ip))
        {
            _log.Warn("p2p", $"La IP externa del router ({upnpIp}) no coincide con la observada por otros nodos ({address.Ip}): posible doble NAT.");
        }
        AnnounceOwnAddresses();
        PeersChanged?.Invoke();
    }

    private void SetPrivate()
    {
        bool wasPublic;
        lock (_reachGate)
        {
            wasPublic = Reachability == Reachability.Public;
            Reachability = Reachability.Private;
        }
        _log.Warn("p2p", (wasPublic ? "El nodo dejó de ser accesible desde Internet. " : "Ningún par pudo conectarse a esta PC desde Internet. ") +
                         $"Si querés recibir conexiones directas, abrí el puerto TCP {ListeningPort} en el router y el firewall. " +
                         (_config.RelayClient ? "Mientras tanto se usará un relay para que otros nodos puedan llegar a esta PC." : "El cliente de relay está desactivado."));
        PeersChanged?.Invoke();
    }

    private async Task UpnpLoopAsync(CancellationToken token)
    {
        _upnp = new UpnpPortMapper();
        var first = true;
        var failures = 0;
        while (!token.IsCancellationRequested)
        {
            var ok = await _upnp.MapAsync(ListeningPort, token).ConfigureAwait(false);
            if (first || ok != (_upnp.State == UpnpState.Mapped))
            {
                if (ok)
                {
                    _log.Success("upnp", $"Puerto TCP {ListeningPort} abierto en el router por UPnP. IP externa del router: {_upnp.ExternalIp?.ToString() ?? "desconocida"}.");
                    if (_upnp.BehindCarrierNat && !_cgnatWarned)
                    {
                        _cgnatWarned = true;
                        _log.Warn("upnp", $"El router recibe una IP privada ({_upnp.ExternalIp}): tu proveedor usa CGNAT o hay doble NAT. " +
                                          "Abrir puertos no alcanza; otros nodos llegarán a esta PC a través de un relay.");
                    }
                }
                else if (first)
                {
                    _log.Info("upnp", $"UPnP no disponible ({_upnp.LastError}). Para recibir conexiones directas abrí el puerto TCP {ListeningPort} en el router; si no, se usará un relay.");
                }
            }
            if (ok && first) _nextDialBackRound = DateTimeOffset.MinValue;
            first = false;
            failures = ok ? 0 : failures + 1;
            var wait = ok ? TimeSpan.FromMinutes(20) : failures <= 3 ? TimeSpan.FromMinutes(2) : TimeSpan.FromMinutes(30);
            try { await Task.Delay(wait, token).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
        }
    }

    // =====================================================================
    // Sincronización y difusión
    // =====================================================================

    private void MaybeStartSync(PeerConnection? candidate = null)
    {
        lock (_syncGate)
        {
            if (_syncPeer is { IsClosed: false } && DateTimeOffset.UtcNow - _syncPeer.LastSyncActivity < SyncStallTimeout) return;
            var tip = _chain.Tip;
            var best = candidate is { HandshakeComplete: true, IsClosed: false } && candidate.BestWork > tip.TotalWork
                ? candidate
                : _peers.Values.Where(p => p.HandshakeComplete && !p.IsClosed && p.BestWork > tip.TotalWork).MaxBy(p => p.BestWork);
            if (best is null) return;
            BeginSyncLocked(best);
        }
    }

    private void StartSyncWith(PeerConnection peer)
    {
        lock (_syncGate)
        {
            if (_syncPeer is { IsClosed: false } && _syncPeer != peer && DateTimeOffset.UtcNow - _syncPeer.LastSyncActivity < SyncStallTimeout) return;
            BeginSyncLocked(peer);
        }
    }

    private void BeginSyncLocked(PeerConnection peer)
    {
        if (_syncPeer != peer)
        {
            _log.Info("sync", $"Sincronizando con {peer.DisplayAddress} (altura anunciada {peer.BestHeight}, sin verificar).");
        }
        _syncPeer = peer;
        peer.Syncing = true;
        peer.LastSyncActivity = DateTimeOffset.UtcNow;
        peer.Send(new GetBlocksMessage(_chain.GetLocator(), Hash256.Zero));
    }

    /// <summary>
    /// El par ya no tiene bloques que nos falten: lo que haya anunciado de más no era cierto (o era una
    /// rama inválida), así que su trabajo se ajusta a lo verificado y no se vuelve a elegir en bucle.
    /// </summary>
    private void CapAnnouncedWork(PeerConnection peer)
    {
        var tip = _chain.Tip;
        if (peer.BestWork > tip.TotalWork) peer.BestWork = tip.TotalWork;
        if (peer.BestHeight > tip.Height) peer.BestHeight = tip.Height;
    }

    private void FinishSync(PeerConnection peer)
    {
        peer.Syncing = false;
        CapAnnouncedWork(peer);
        lock (_syncGate)
        {
            if (_syncPeer == peer)
            {
                _syncPeer = null;
                _log.Success("sync", $"Cadena sincronizada con {peer.DisplayAddress} en la altura {_chain.Height}.");
            }
        }
    }

    public bool IsSyncing
    {
        get
        {
            lock (_syncGate) return _syncPeer is { IsClosed: false };
        }
    }

    /// <summary>Mayor altura conocida (la anunciada por pares es solo orientativa y se corrige al sincronizar).</summary>
    public long BestKnownHeight => Math.Max(_chain.Height, _peers.Values.Where(p => p.HandshakeComplete).Select(p => p.BestHeight).DefaultIfEmpty(0).Max());

    private void OnChainUpdated(ChainUpdate update)
    {
        lock (_rejectGate) _recentRejects.Clear();
        var tip = update.Tip;
        if (tip.Timestamp < DateTimeOffset.UtcNow.ToUnixTimeSeconds() - _network.TargetSpacingSeconds * 12L) return;
        foreach (var peer in _peers.Values)
        {
            if (!peer.HandshakeComplete || peer.Knows(tip.Hash)) continue;
            peer.MarkKnown(tip.Hash);
            peer.Send(new InvMessage(InvType.Block, [tip.Hash]));
        }
    }

    public void BroadcastTransaction(Transaction tx) => RelayTransaction(tx, except: null);

    private void RelayTransaction(Transaction tx, PeerConnection? except)
    {
        foreach (var peer in _peers.Values)
        {
            if (peer == except || !peer.HandshakeComplete || peer.Knows(tx.Id)) continue;
            peer.MarkKnown(tx.Id);
            peer.Send(new InvMessage(InvType.Tx, [tx.Id]));
        }
    }

    // =====================================================================
    // Bucles de mantenimiento
    // =====================================================================

    private async Task OutboundLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                await FillOutboundAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _log.Error("p2p", $"Error en conexiones salientes: {ex.Message}");
            }
            try { await Task.Delay(TimeSpan.FromSeconds(2), token).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
        }
    }

    private async Task FillOutboundAsync()
    {
        var outbound = _peers.Values.Count(p => !p.Inbound) + _connecting.Count;
        if (outbound >= _config.MaxOutbound) return;

        var connectedCount = _peers.Values.Count(p => p.HandshakeComplete);
        if ((_book.Count < 3 || connectedCount == 0) && DateTimeOffset.UtcNow - _lastSeedQuery > TimeSpan.FromMinutes(2))
        {
            _lastSeedQuery = DateTimeOffset.UtcNow;
            await QuerySeedsAsync().ConfigureAwait(false);
        }

        var exclude = new HashSet<string>(_connecting.Keys);
        var groups = new HashSet<string>();
        var connectedIds = new HashSet<PeerId>();
        foreach (var p in _peers.Values)
        {
            if (p.DialedAddress is not null) exclude.Add(p.DialedAddress.Key);
            if (p.ListenAddress is not null) exclude.Add(p.ListenAddress.Key);
            if (p.RemoteHello?.NodeId is { } id) connectedIds.Add(id);
            if (p.ExpectedPeerId is { } expected) connectedIds.Add(expected);
            if (!p.Inbound && !p.IsRelayed) groups.Add(AddressBook.NetworkGroup(p.RemoteIp));
        }
        var circuits = _peers.Values.Count(p => !p.Inbound && p.IsRelayed);

        foreach (var candidate in _book.PickCandidates(exclude, 16))
        {
            if (outbound >= _config.MaxOutbound) break;
            if (candidate.NodeId is { } id && connectedIds.Contains(id)) continue;
            var ip = candidate.Endpoint.Ip;
            if (IPAddress.IsLoopback(ip) && !_config.AllowLoopbackPeers && !_manualKeys.ContainsKey(candidate.Key)) continue;
            if (candidate.IsCircuit)
            {
                if (circuits >= Math.Max(2, _config.MaxOutbound / 2)) continue;
                circuits++;
            }
            else
            {
                var group = AddressBook.NetworkGroup(ip);
                if (!AddressBook.IsPrivate(ip) && !groups.Add(group)) continue;
            }
            outbound++;
            if (candidate.NodeId is { } dialing) connectedIds.Add(dialing);
            _ = TryConnectAsync(candidate);
        }
    }

    private async Task QuerySeedsAsync()
    {
        foreach (var text in _config.Bootstrap.Concat(_config.AddNodes))
        {
            if (!AddressSpec.TryParse(text, _network.DefaultPort, out var spec, out _) || spec!.IsLiteral) continue;
            foreach (var address in await ResolveAsync(spec).ConfigureAwait(false)) AddManual(address, "bootstrap");
        }
        foreach (var seed in _config.Seeds.Concat(_network.Seeds).Distinct())
        {
            if (!AddressSpec.TryParse(seed, _network.DefaultPort, out var spec, out _))
            {
                _log.Warn("p2p", $"Semilla inválida: {seed}");
                continue;
            }
            var resolved = await ResolveAsync(spec!).ConfigureAwait(false);
            if (resolved.Count == 0) _log.Warn("p2p", $"No se pudo resolver el nodo semilla {spec!.Host}.");
            foreach (var address in resolved) _book.Add(address, "seed");
        }
    }

    /// <summary>
    /// Busca nodos de la misma red en la DHT y, si este nodo acepta conexiones, publica su puerto.
    /// Frecuente mientras no haya pares; cada 15 minutos (renovación del anuncio) cuando ya está conectado.
    /// </summary>
    private async Task DhtLoopAsync(CancellationToken token)
    {
        var infoHash = MainlineDht.InfoHashFor(_network.Name, _chain.GenesisHash.ToString());
        _log.Info("dht", "Buscando otros nodos SHENT por Internet (DHT pública)…");
        var emptyRounds = 0;
        var announcedOnce = false;
        while (!token.IsCancellationRequested && _dht is { } dht)
        {
            int? announcePort = IsListening && Reachability != Reachability.Private ? ListeningPort : null;
            try
            {
                var result = await dht.LookupAsync(infoHash, announcePort, token).ConfigureAwait(false);
                var added = 0;
                foreach (var endpoint in result.Peers)
                {
                    var address = new PeerAddress(endpoint.Address, endpoint.Port);
                    if (_selfKeys.ContainsKey(((NodeAddress)address).Key) || IsOwnEndpoint(address)) continue;
                    if (!AddressBook.IsAcceptable(address, _config.AllowLoopbackPeers) || _book.IsBanned(address.Ip)) continue;
                    if (_book.Add(address, "dht")) added++;
                }
                _dhtStatus = _dhtStatus with
                {
                    LastLookup = DateTimeOffset.UtcNow,
                    LastPeersFound = result.Peers.Count,
                    LastResponses = result.Responses,
                    Announcing = result.Announced > 0,
                    TotalAdded = _dhtStatus.TotalAdded + added,
                };
                if (result.Responses == 0)
                {
                    _log.Warn("dht", "La DHT no respondió. Puede que un firewall bloquee el tráfico UDP saliente; se reintentará.");
                }
                else if (added > 0)
                {
                    _log.Info("dht", $"La DHT devolvió {added} nodo(s) nuevo(s). Se verificarán con el handshake antes de usarlos.");
                }
                if (result.Announced > 0 && !announcedOnce)
                {
                    announcedOnce = true;
                    _log.Info("dht", $"Puerto TCP {announcePort} publicado en {result.Announced} nodos de la DHT: otros nodos SHENT podrán encontrar este.");
                }
                emptyRounds = result.Peers.Count == 0 ? emptyRounds + 1 : 0;
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                _log.Warn("dht", $"Error en la búsqueda por DHT: {ex.Message}");
                emptyRounds++;
            }

            var connected = _peers.Values.Count(p => p.HandshakeComplete);
            var delay = connected switch
            {
                0 => TimeSpan.FromSeconds(Math.Min(300, 20 * Math.Pow(2, Math.Min(emptyRounds, 4)))),
                < 3 => TimeSpan.FromMinutes(3),
                _ => TimeSpan.FromMinutes(15),
            };
            try { await Task.Delay(delay, token).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
        }
    }

    private bool IsOwnEndpoint(PeerAddress address)
    {
        if (!IsListening || address.Port != ListeningPort) return false;
        var ip = PeerAddress.Normalize(address.Ip);
        if (PublicAddress is { } pub && PeerAddress.Normalize(pub.Ip).Equals(ip)) return true;
        if (UpnpExternalIp is { } external && PeerAddress.Normalize(external).Equals(ip)) return true;
        return false;
    }

    private async Task MaintenanceLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try { await Task.Delay(TimeSpan.FromSeconds(5), token).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }

            try
            {
                Maintain();
            }
            catch (Exception ex)
            {
                _log.Error("p2p", $"Error de mantenimiento: {ex.Message}");
            }
        }
    }

    private void Maintain()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var peer in _peers.Values)
        {
            if (!peer.HandshakeComplete && now - peer.ConnectedAt > HandshakeTimeout)
            {
                peer.Close("Tiempo de handshake agotado.");
                continue;
            }
            if (now - peer.LastReceived > IdleTimeout)
            {
                peer.Close("Par inactivo.");
                continue;
            }
            if (peer.HandshakeComplete && peer.PendingPingNonce == 0 && now - peer.LastReceived > PingInterval / 2
                && Environment.TickCount64 - peer.PingSentTicks > PingInterval.TotalMilliseconds)
            {
                var nonce = BitConverter.ToUInt64(RandomNumberGenerator.GetBytes(8)) | 1;
                peer.PendingPingNonce = nonce;
                peer.PingSentTicks = Environment.TickCount64;
                peer.Send(new PingMessage(nonce));
            }
            else if (peer.PendingPingNonce != 0 && Environment.TickCount64 - peer.PingSentTicks > 30_000)
            {
                peer.PendingPingNonce = 0;
            }
        }

        lock (_syncGate)
        {
            if (_syncPeer is not null && now - _syncPeer.LastSyncActivity > SyncStallTimeout)
            {
                _log.Warn("sync", $"La sincronización con {_syncPeer.DisplayAddress} se detuvo; probando con otro par.");
                _syncPeer.Syncing = false;
                lock (_syncPeer.BlocksInFlight) _syncPeer.BlocksInFlight.Clear();
                CapAnnouncedWork(_syncPeer);
                _syncPeer = null;
            }
        }
        MaybeStartSync();

        ExpireEmbargoes();
        var tick = Environment.TickCount64;
        foreach (var (hash, at) in _txRequested)
        {
            if (tick - at > TxRequestTimeout.TotalMilliseconds * 2) _txRequested.TryRemove(hash, out _);
        }
        foreach (var (nonce, probe) in _pendingProbes)
        {
            if (now - probe.Created > TimeSpan.FromMinutes(1)) _pendingProbes.TryRemove(nonce, out _);
        }

        if (IsListening && now >= _nextDialBackRound && _peers.Values.Any(CanAskDialBack)) StartDialBackRound(now);
        MaintainRelayReservations(now);

        var connected = _peers.Values.Where(p => p.HandshakeComplete).ToList();
        var exchangeInterval = connected.Count < 3 ? TimeSpan.FromMinutes(1) : TimeSpan.FromMinutes(10);
        if (connected.Count > 0 && now - _lastPeerExchange > exchangeInterval)
        {
            _lastPeerExchange = now;
            connected[RandomNumberGenerator.GetInt32(connected.Count)].Send(new GetPeersMessage());
        }

        if (now - _lastBookSave > TimeSpan.FromMinutes(1))
        {
            _lastBookSave = now;
            try { _book.Save(); }
            catch (IOException ex) { _log.Warn("p2p", $"No se pudo guardar la lista de pares: {ex.Message}"); }
        }
    }

    // =====================================================================
    // Estado (para la interfaz y la API local; nunca incluye claves)
    // =====================================================================

    public List<PeerSnapshot> GetPeers() => _peers.Values
        .Select(p => new PeerSnapshot(p.Id, p.DisplayAddress, p.Inbound, p.RemoteHello?.UserAgent ?? "—", p.BestHeight,
            p.PingMs, p.ConnectedAt, p.Syncing, p.Misbehavior, p.HandshakeComplete,
            p.RemotePeerId?.ToString(), p.IsRelayed, p.RemoteHello?.Services ?? NodeServices.None, p.RemoteHello?.TipHash.ToString()))
        .OrderByDescending(p => p.HandshakeComplete)
        .ThenBy(p => p.ConnectedAt)
        .ToList();

    /// <summary>Direcciones de la red local (útiles para conectar PCs de la misma casa u oficina).</summary>
    public List<string> GetLocalAddresses()
    {
        if (!IsListening) return [];
        var result = new List<string>();
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                foreach (var unicast in nic.GetIPProperties().UnicastAddresses)
                {
                    var ip = unicast.Address;
                    if (ip.AddressFamily != AddressFamily.InterNetwork) continue;
                    result.Add(new NodeAddress(new PeerAddress(ip, ListeningPort), PeerId).ToMultiaddr());
                }
            }
        }
        catch (NetworkInformationException)
        {
        }
        return result.Distinct().ToList();
    }

    /// <summary>Direcciones para compartir con amigos por Internet: pública verificada, announce y circuitos de relay.</summary>
    public List<string> GetPublicAddresses() => GetOwnAddresses().Select(a => a.ToMultiaddr()).ToList();

    public List<RelayReservationInfo> GetRelayReservations() => _reservations.Values
        .Where(r => !r.Relay.IsClosed && r.Relay.RemotePeerId is not null)
        .Select(r => new RelayReservationInfo(r.Relay.RemotePeerId!.ToString(), r.RelayEndpoint.ToString(),
            new NodeAddress(r.RelayEndpoint, r.Relay.RemotePeerId, PeerId).ToMultiaddr(), r.Expires))
        .ToList();

    public void DisconnectPeer(Guid id)
    {
        if (_peers.TryGetValue(id, out var peer)) peer.Close("Desconectado manualmente.");
    }

    public async ValueTask DisposeAsync()
    {
        _chain.ChainUpdated -= OnChainUpdated;
        _cts.Cancel();
        _listener?.Stop();
        _lan?.Dispose();
        _dht?.Dispose();
        foreach (var peer in _peers.Values) peer.Close("Nodo detenido.");
        try { _book.Save(); } catch (IOException) { }
        if (_upnp is not null) await _upnp.DisposeAsync().ConfigureAwait(false);
        await Task.Delay(50).ConfigureAwait(false);
        if (_ownsIdentity) _identity.Dispose();
        _cts.Dispose();
    }
}
