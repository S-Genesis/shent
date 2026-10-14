using Shent.Core.Api;
using Shent.Core.Chain;
using Shent.Core.Consensus;
using Shent.Core.Crypto;
using Shent.Core.Mempool;
using Shent.Core.Mining;
using Shent.Core.Model;
using Shent.Core.P2P;

namespace Shent.Core.Node;

public sealed record NodeStatus(
    string Network,
    long Height,
    Hash256 TipHash,
    long TipTimestamp,
    double Difficulty,
    double NetworkHashrate,
    int PeersConnected,
    int Inbound,
    int Outbound,
    long BestKnownHeight,
    bool Syncing,
    long IssuedSupply,
    long MaxSupply,
    long CurrentSubsidy,
    long NextHalvingHeight,
    int MempoolCount,
    bool Listening,
    int ListeningPort,
    bool LanDiscovery,
    int KnownAddresses,
    int BannedPeers,
    string PeerId = "",
    Reachability Reachability = Reachability.Unknown,
    string? PublicAddress = null,
    UpnpState Upnp = UpnpState.Disabled,
    bool CarrierNatDetected = false,
    bool RelayService = false,
    int RelayReservations = 0,
    int RelayedPeers = 0,
    DhtStatus? Dht = null);

/// <summary>Nodo SHENT completo: cadena, mempool, red P2P y minero.</summary>
public sealed class ShentNode : IAsyncDisposable
{
    public NodeConfig Config { get; }
    public NetworkParams Network { get; }
    public NodeLog Log { get; }
    public Blockchain Chain { get; }
    public TxMempool Mempool { get; }
    public P2PNetwork P2P { get; }
    public Miner Miner { get; }
    public DiagnosticsServer? Api { get; private set; }

    private readonly NodeIdentity _identity;

    /// <summary>Archivo de identidad P2P del nodo (no contiene fondos; ver <see cref="NodeIdentity"/>).</summary>
    public static string IdentityPath(NodeConfig config) => Path.Combine(config.NetworkDataDirectory, "node_key.json");

    private ShentNode(NodeConfig config, NodeLog log)
    {
        Config = config;
        Network = config.NetworkParams;
        Log = log;
        Directory.CreateDirectory(config.NetworkDataDirectory);

        _identity = NodeIdentity.LoadOrCreate(IdentityPath(config), m => log.Warn("p2p", m));
        Chain = new Blockchain(Network, Path.Combine(config.NetworkDataDirectory, "chain.db"));
        Mempool = new TxMempool(Chain);
        Miner = new Miner(Network.CreatePowHasher(),
            payout => BlockTemplateBuilder.Build(Chain, Mempool, payout, config.UserAgent),
            block => Chain.ProcessBlock(block));
        P2P = new P2PNetwork(config, Network, Chain, Mempool, log, Path.Combine(config.NetworkDataDirectory, "peers.json"), _identity);

        Chain.ChainUpdated += OnChainUpdated;
        Miner.Log += message => log.Info("minería", message);
        Miner.BlockFound += info => log.Success("minería", $"Bloque {info.Height} minado por esta PC: +{Money.Format(info.Reward)} SHENT.");
    }

    public static ShentNode Start(NodeConfig config, NodeLog? log = null)
    {
        log ??= new NodeLog();
        var node = new ShentNode(config, log);
        log.Info("nodo", $"SHENT {config.UserAgent} en {node.Network.Name}. Altura local: {node.Chain.Height}. Datos: {config.NetworkDataDirectory}");
        var (consistent, sum, issued, _) = node.Chain.Reconcile();
        if (!consistent)
        {
            log.Error("nodo", $"Conciliación fallida: saldos {Money.Format(sum)} ≠ emitido {Money.Format(issued)}. Revisá la base de datos.");
        }
        node.P2P.Start();
        if (config.Api) node.StartApi();

        if (config.Mine && config.MiningAddress is not null)
        {
            if (Address.TryParse(config.MiningAddress, node.Network.AddressHrp, out var payout, out var error))
            {
                node.Miner.Start(payout!, config.MiningThreads, config.MiningIntensity);
            }
            else
            {
                log.Error("minería", $"Dirección de minado inválida: {error}");
            }
        }
        return node;
    }

    /// <summary>
    /// Si el puerto está ocupado (por ejemplo, una instancia anterior que todavía se está cerrando),
    /// se reintenta en segundo plano en lugar de quedar sin API hasta reiniciar.
    /// </summary>
    private void StartApi()
    {
        var api = new DiagnosticsServer(Config.EffectiveApiPort, new Dictionary<string, Func<object>>
        {
            ["/api/p2p/status"] = GetP2PStatus,
            ["/api/p2p/peers"] = GetP2PPeers,
            ["/api/p2p/info"] = GetP2PInfo,
            ["/api/chain/checkpoints"] = GetCheckpointCandidates,
        }, Log);
        if (api.Start())
        {
            Api = api;
            return;
        }
        var token = _lifetime.Token;
        _ = Task.Run(async () =>
        {
            while (!token.IsCancellationRequested)
            {
                try { await Task.Delay(ApiRetryInterval, token).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }
                if (!api.Start(quiet: true)) continue;
                lock (_apiGate)
                {
                    if (token.IsCancellationRequested) break;
                    Api = api;
                    return;
                }
            }
            await api.DisposeAsync().ConfigureAwait(false);
        });
    }

    internal static TimeSpan ApiRetryInterval { get; set; } = TimeSpan.FromSeconds(30);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly object _apiGate = new();

    private void OnChainUpdated(ChainUpdate update)
    {
        Miner.RefreshTemplate();
        var tip = update.Tip;
        var recent = tip.Timestamp > DateTimeOffset.UtcNow.ToUnixTimeSeconds() - Network.TargetSpacingSeconds * 12L;
        if (update.Disconnected.Count > 0)
        {
            Log.Warn("cadena", $"Reorganización: {update.Disconnected.Count} bloque(s) reemplazados por una cadena con más trabajo.");
        }
        if (recent || tip.Height % 500 == 0)
        {
            Log.Info("cadena", $"Nueva punta: bloque {tip.Height} ({tip.Hash.ToShortString()}).");
        }
    }

    public MempoolResult SubmitTransaction(Transaction tx)
    {
        var result = P2P.SubmitLocalTransaction(tx);
        if (result.Accepted)
        {
            Log.Info("billetera", P2P.Dandelion.Enabled && Mempool.IsPrivate(tx.Id)
                ? $"Transacción {tx.Id.ToShortString()} enviada por Dandelion++: pasa por otros nodos antes de difundirse."
                : $"Transacción {tx.Id.ToShortString()} enviada a la red.");
        }
        return result;
    }

    public NodeStatus GetStatus()
    {
        var tip = Chain.Tip;
        var peers = P2P.GetPeers().Where(p => p.HandshakeComplete).ToList();
        var nextHeight = tip.Height + 1;
        return new NodeStatus(
            Network.Name,
            tip.Height,
            tip.Hash,
            tip.Timestamp,
            Target.Difficulty(Chain.GetTemplateContext().Bits),
            Chain.EstimateNetworkHashrate(),
            peers.Count,
            peers.Count(p => p.Inbound),
            peers.Count(p => !p.Inbound),
            P2P.BestKnownHeight,
            P2P.IsSyncing,
            Chain.IssuedSupply,
            Network.MaxSupply,
            Emission.SubsidyAt(Network, nextHeight),
            Emission.NextHalvingHeight(Network, nextHeight),
            Mempool.Count,
            P2P.IsListening,
            P2P.ListeningPort,
            P2P.LanDiscoveryActive,
            P2P.AddressBook.Count,
            P2P.AddressBook.BannedCount,
            P2P.PeerId.ToString(),
            P2P.Reachability,
            P2P.PublicAddress is { } pub ? new NodeAddress(pub, P2P.PeerId).ToMultiaddr() : null,
            P2P.UpnpState,
            P2P.CarrierNatDetected,
            P2P.Relay is not null && P2P.IsListening,
            P2P.GetRelayReservations().Count,
            peers.Count(p => p.Relayed),
            P2P.Dht);
    }

    // ---------------------------------------------------------------------
    // Respuestas de la API local (/api/p2p/*). Solo datos públicos: nunca claves ni billeteras.
    // ---------------------------------------------------------------------

    public object GetP2PStatus()
    {
        var s = GetStatus();
        var relay = P2P.Relay;
        return new
        {
            network = s.Network,
            peerId = s.PeerId,
            protocolVersion = Protocol.Version,
            height = s.Height,
            tipHash = s.TipHash.ToString(),
            bestKnownHeight = s.BestKnownHeight,
            bestKnownHeightNote = "Altura máxima anunciada por pares; no está verificada hasta descargar y validar los bloques.",
            syncing = s.Syncing,
            syncProgress = s.BestKnownHeight > 0 ? Math.Round(Math.Min(1.0, (double)s.Height / s.BestKnownHeight), 4) : 1.0,
            peers = new { connected = s.PeersConnected, inbound = s.Inbound, outbound = s.Outbound, relayed = s.RelayedPeers },
            knownAddresses = s.KnownAddresses,
            bannedPeers = s.BannedPeers,
            listening = s.Listening,
            listenPort = s.ListeningPort,
            reachability = s.Reachability,
            publicAddress = s.PublicAddress,
            upnp = new { state = s.Upnp, externalIp = P2P.UpnpExternalIp?.ToString() },
            carrierNatDetected = s.CarrierNatDetected,
            dht = new
            {
                running = P2P.Dht.Running,
                lastLookup = P2P.Dht.LastLookup,
                lastPeersFound = P2P.Dht.LastPeersFound,
                lastResponses = P2P.Dht.LastResponses,
                announcing = P2P.Dht.Announcing,
                totalAdded = P2P.Dht.TotalAdded,
                note = "La DHT solo sugiere direcciones; cada nodo se verifica con el handshake firmado y cada bloque se valida localmente.",
            },
            privacy = new
            {
                proxyMode = P2P.ProxyMode,
                proxy = P2P.ProxyEndpoint,
                dandelion = P2P.Dandelion.Enabled,
                dandelionStemPeers = P2P.Dandelion.StemPeers,
                transactionsInStemPhase = P2P.Dandelion.PrivateTransactions,
                note = "Dandelion++ oculta qué nodo creó cada transacción; el proxy (Tor) oculta tu IP ante los demás nodos. " +
                       "Las direcciones y los importes siguen siendo públicos en la cadena.",
            },
            relay = new
            {
                serviceEnabled = s.RelayService,
                activeCircuits = relay?.ActiveCircuits ?? 0,
                reservationsServed = relay?.ReservationCount ?? 0,
                reservationsHeld = s.RelayReservations,
            },
            mempool = s.MempoolCount,
        };
    }

    public object GetP2PPeers() => P2P.GetPeers()
        .Where(p => p.HandshakeComplete)
        .Select(p => new
        {
            peerId = p.PeerId,
            address = p.Address,
            direction = p.Inbound ? "inbound" : "outbound",
            relayed = p.Relayed,
            userAgent = p.UserAgent,
            announcedHeight = p.BestHeight,
            announcedTipHash = p.TipHash,
            pingMs = p.PingMs,
            connectedAt = p.ConnectedAt,
            syncing = p.Syncing,
            misbehavior = p.Misbehavior,
            services = p.Services.ToString(),
        })
        .ToList();

    public object GetP2PInfo() => new
    {
        peerId = P2P.PeerId.ToString(),
        network = Network.Name,
        userAgent = Config.UserAgent,
        protocolVersion = Protocol.Version,
        p2pPort = P2P.ListeningPort,
        lanDiscoveryPort = Network.LanDiscoveryPort,
        apiPort = Api?.Port,
        listening = P2P.IsListening,
        reachability = P2P.Reachability,
        localAddresses = P2P.GetLocalAddresses(),
        publicAddresses = P2P.GetPublicAddresses(),
        relayReservations = P2P.GetRelayReservations(),
        relayServiceEnabled = P2P.Relay is not null && P2P.IsListening,
        bootstrap = Config.Bootstrap,
        dhtDiscovery = P2P.Dht.Running,
        howToConnect = P2P.Dht.Running
            ? "Los nodos se encuentran solos por la DHT y la red local. Opcional: compartir una dirección de publicAddresses como BOOTSTRAP_NODES."
            : "Compartí una dirección de publicAddresses (o de localAddresses si están en la misma red). " +
              "El otro nodo la usa como BOOTSTRAP_NODES o con --bootstrap.",
    };

    /// <summary>
    /// Bloques de la cadena principal aptos para fijarse como puntos de control en el próximo release:
    /// cada <see cref="CheckpointInterval"/> bloques y con más confirmaciones que la reorganización máxima
    /// (a esa profundidad la cadena ya no puede cambiar en un nodo sincronizado).
    /// </summary>
    public object GetCheckpointCandidates()
    {
        var tip = Chain.Tip;
        var deepest = tip.Height - Network.MaxForkDepth;
        var list = new List<object>();
        for (var h = CheckpointInterval; h <= deepest; h += CheckpointInterval)
        {
            if (Chain.GetBlockAtHeight(h) is { } block) list.Add(new { height = h, hash = block.Hash.ToString() });
        }
        return new
        {
            network = Network.Name,
            genesisHash = Chain.GenesisHash.ToString(),
            height = tip.Height,
            minConfirmations = Network.MaxForkDepth,
            syncing = P2P.IsSyncing,
            checkpoints = list,
        };
    }

    public const long CheckpointInterval = 1000;

    public async ValueTask DisposeAsync()
    {
        Miner.Stop();
        DiagnosticsServer? api;
        lock (_apiGate)
        {
            _lifetime.Cancel();
            api = Api;
        }
        if (api is not null) await api.DisposeAsync().ConfigureAwait(false);
        await P2P.DisposeAsync().ConfigureAwait(false);
        _identity.Dispose();
        Chain.Dispose();
        Log.Info("nodo", "Nodo detenido.");
    }
}
