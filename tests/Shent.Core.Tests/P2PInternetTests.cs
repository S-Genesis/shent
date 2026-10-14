using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Numerics;
using System.Text.Json;
using Shent.Core.Api;
using Shent.Core.Chain;
using Shent.Core.Consensus;
using Shent.Core.Crypto;
using Shent.Core.Mining;
using Shent.Core.Model;
using Shent.Core.Node;
using Shent.Core.P2P;

namespace Shent.Core.Tests;

/// <summary>
/// Pruebas de varios nodos reales en la misma máquina (127.0.0.1, puertos distintos, carpetas distintas):
/// identidad, descubrimiento, propagación, sincronización, reconexión, relay y API local.
/// </summary>
public sealed class P2PInternetTests : IAsyncDisposable
{
    private readonly List<ShentNode> _nodes = [];
    private readonly List<string> _dirs = [];

    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    private NodeConfig NewConfig(Action<NodeConfig>? customize = null)
    {
        var dir = Path.Combine(Path.GetTempPath(), "shent-p2p-tests", Guid.NewGuid().ToString("N"));
        _dirs.Add(dir);
        var config = new NodeConfig
        {
            Network = "regtest",
            DataDirectory = dir,
            P2PPort = FreePort(),
            LanDiscovery = false,
            AllowLoopbackPeers = true,
            Upnp = false,
            Api = false,
        };
        customize?.Invoke(config);
        return config;
    }

    private ShentNode Start(NodeConfig config)
    {
        var node = ShentNode.Start(config);
        _nodes.Add(node);
        return node;
    }

    private ShentNode StartNode(Action<NodeConfig>? customize = null) => Start(NewConfig(customize));

    private async Task Stop(ShentNode node)
    {
        _nodes.Remove(node);
        await node.DisposeAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
    }

    private static string AddressOf(ShentNode node) => $"/ip4/127.0.0.1/tcp/{node.P2P.ListeningPort}/p2p/{node.P2P.PeerId}";

    private static bool ConnectedTo(ShentNode node, ShentNode other) =>
        node.P2P.GetPeers().Any(p => p.HandshakeComplete && p.PeerId == other.P2P.PeerId.ToString());

    private static Block MineBlock(ShentNode node, Address payout)
    {
        var template = BlockTemplateBuilder.Build(node.Chain, node.Mempool, payout, "test");
        var pow = node.Network.CreatePowHasher();
        var bytes = template.HeaderBytes;
        for (ulong nonce = 0; ; nonce++)
        {
            BlockHeader.WriteNonce(bytes, nonce);
            if (PowHasher.MeetsTarget(pow.Hash(bytes), template.Target))
            {
                return new Block { Header = template.Block.Header.WithNonce(nonce), Transactions = template.Block.Transactions };
            }
        }
    }

    private static async Task WaitUntil(Func<bool> condition, int seconds = 30, string? what = null)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException($"La condición no se cumplió a tiempo{(what is null ? "" : $": {what}")}.");
            await Task.Delay(50);
        }
    }

    // =====================================================================
    // Identidad y direcciones
    // =====================================================================

    [Fact]
    public void NodeIdentityPersistsAndPeerIdRoundTrips()
    {
        var dir = Path.Combine(Path.GetTempPath(), "shent-p2p-tests", Guid.NewGuid().ToString("N"));
        _dirs.Add(dir);
        var path = Path.Combine(dir, "node_key.json");
        PeerId first;
        using (var a = NodeIdentity.LoadOrCreate(path)) first = a.PeerId;
        using (var b = NodeIdentity.LoadOrCreate(path)) Assert.Equal(first, b.PeerId);

        var text = first.ToString();
        Assert.StartsWith("peer1", text);
        Assert.True(PeerId.TryParse(text, out var parsed));
        Assert.Equal(first, parsed);
        var corrupted = text[..^1] + (text[^1] == 'q' ? 'p' : 'q');
        Assert.False(PeerId.TryParse(corrupted, out _));
        Assert.False(PeerId.TryParse("shent1qqqqqq", out _));

        File.WriteAllText(path, "{ dañado");
        using var c = NodeIdentity.LoadOrCreate(path);
        Assert.NotEqual(first, c.PeerId);
        Assert.True(File.Exists(path + ".corrupt"));
    }

    [Fact]
    public void MultiaddrParsingAcceptsValidFormsAndRejectsInvalidOnes()
    {
        using var id = NodeIdentity.Ephemeral();
        using var relay = NodeIdentity.Ephemeral();
        Assert.True(AddressSpec.TryParse($"/ip4/203.0.113.10/tcp/37373/p2p/{id.PeerId}", 1, out var direct, out _));
        Assert.Equal(("203.0.113.10", 37373, id.PeerId), (direct!.Host, direct.Port, direct.PeerId));

        Assert.True(AddressSpec.TryParse($"/dns4/nodo.example.org/tcp/47373/p2p/{id.PeerId}", 1, out var dns, out _));
        Assert.False(dns!.IsLiteral);

        Assert.True(AddressSpec.TryParse($"/ip4/203.0.113.10/tcp/37373/p2p/{relay.PeerId}/p2p-circuit/p2p/{id.PeerId}", 1, out var circuit, out _));
        Assert.Equal(relay.PeerId, circuit!.PeerId);
        Assert.Equal(id.PeerId, circuit.CircuitTarget);

        Assert.True(AddressSpec.TryParse("/ip6/2001:db8::1/tcp/37373", 1, out var v6, out _));
        Assert.Equal("2001:db8::1", v6!.Host);
        Assert.True(AddressSpec.TryParse("198.51.100.4:4000", 37373, out var classic, out _));
        Assert.Equal(4000, classic!.Port);
        Assert.True(AddressSpec.TryParse("198.51.100.4", 37373, out var noPort, out _));
        Assert.Equal(37373, noPort!.Port);
        Assert.True(AddressSpec.TryParse("seed.example.org:37373", 1, out var host, out _));
        Assert.Equal("seed.example.org", host!.Host);

        Assert.False(AddressSpec.TryParse("/ip4/999.1.1.1/tcp/37373", 1, out _, out _));
        Assert.False(AddressSpec.TryParse("/ip4/203.0.113.10/udp/37373", 1, out _, out _));
        Assert.False(AddressSpec.TryParse("/ip4/203.0.113.10/tcp/70000", 1, out _, out _));
        Assert.False(AddressSpec.TryParse("/ip4/203.0.113.10/tcp/37373/p2p/peer1invalido", 1, out _, out _));
        Assert.False(AddressSpec.TryParse($"/ip4/203.0.113.10/tcp/37373/p2p-circuit/p2p/{id.PeerId}", 1, out _, out var error));
        Assert.NotNull(error);

        var formatted = new NodeAddress(new PeerAddress(IPAddress.Parse("203.0.113.10"), 37373), relay.PeerId, id.PeerId).ToMultiaddr();
        Assert.Equal($"/ip4/203.0.113.10/tcp/37373/p2p/{relay.PeerId}/p2p-circuit/p2p/{id.PeerId}", formatted);
    }

    [Fact]
    public void EnvironmentVariablesConfigureBootstrapAndPorts()
    {
        var env = new Dictionary<string, string>
        {
            ["BOOTSTRAP_NODES"] = "/ip4/203.0.113.1/tcp/37373, /dns4/nodo.example.org/tcp/37373",
            ["P2P_PORT"] = "40000",
            ["SHENT_RELAY"] = "1",
            ["SHENT_UPNP"] = "0",
            ["SHENT_API_PORT"] = "40007",
        };
        var config = new NodeConfig();
        config.ApplyEnvironment(name => env.GetValueOrDefault(name));
        Assert.Equal(2, config.Bootstrap.Count);
        Assert.Equal(40000, config.EffectivePort);
        Assert.True(config.RelayService);
        Assert.False(config.Upnp);
        Assert.Equal(40007, config.EffectiveApiPort);

        var dir = Path.Combine(Path.GetTempPath(), "shent-p2p-tests", Guid.NewGuid().ToString("N"));
        _dirs.Add(dir);
        config.DataDirectory = dir;
        config.Save();
        var loaded = NodeConfig.Load(dir);
        Assert.Equal(config.Bootstrap, loaded.Bootstrap);
        Assert.True(loaded.RelayService);
        Assert.False(loaded.Upnp);
    }

    [Fact]
    public void UpnpDeviceDescriptionIsParsed()
    {
        const string ssdp = "HTTP/1.1 200 OK\r\nCACHE-CONTROL: max-age=120\r\nLOCATION: http://192.168.1.1:5000/rootDesc.xml\r\nST: urn:schemas-upnp-org:device:InternetGatewayDevice:1\r\n\r\n";
        var location = UpnpPortMapper.ParseLocation(ssdp);
        Assert.Equal(new Uri("http://192.168.1.1:5000/rootDesc.xml"), location);

        const string xml = """
            <?xml version="1.0"?>
            <root xmlns="urn:schemas-upnp-org:device-1-0">
              <device>
                <deviceList><device><deviceList><device>
                  <serviceList>
                    <service>
                      <serviceType>urn:schemas-upnp-org:service:WANIPConnection:1</serviceType>
                      <controlURL>/ctl/IPConn</controlURL>
                    </service>
                  </serviceList>
                </device></deviceList></device></deviceList>
              </device>
            </root>
            """;
        var service = UpnpPortMapper.FindWanService(xml, location!);
        Assert.NotNull(service);
        Assert.Equal(new Uri("http://192.168.1.1:5000/ctl/IPConn"), service!.Value.ControlUrl);
        Assert.Contains("WANIPConnection", service.Value.ServiceType);

        // Un controlURL que apunta a otro host se descarta.
        Assert.Null(UpnpPortMapper.FindWanService(xml.Replace("/ctl/IPConn", "http://203.0.113.9/ctl"), location!));
        Assert.True(AddressBook.IsCarrierGradeNat(IPAddress.Parse("100.64.10.20")));
        Assert.False(AddressBook.IsCarrierGradeNat(IPAddress.Parse("100.128.0.1")));
    }

    // =====================================================================
    // Conexión, descubrimiento y propagación
    // =====================================================================

    [Fact]
    public async Task HandshakeVerifiesPeerIdAndRejectsImpostorAddress()
    {
        var nodeA = StartNode();
        using var other = NodeIdentity.Ephemeral();
        var wrong = $"/ip4/127.0.0.1/tcp/{nodeA.P2P.ListeningPort}/p2p/{other.PeerId}";
        var nodeB = StartNode(c => c.Bootstrap.Add(wrong));

        await Task.Delay(3000);
        Assert.False(ConnectedTo(nodeB, nodeA));

        Assert.True(await nodeB.P2P.AddPeerAsync(AddressOf(nodeA)));
        await WaitUntil(() => ConnectedTo(nodeB, nodeA) && ConnectedTo(nodeA, nodeB));
        Assert.Equal(nodeA.P2P.PeerId.ToString(), nodeB.P2P.GetPeers().Single(p => p.HandshakeComplete).PeerId);
        Assert.Equal(1, nodeA.GetStatus().PeersConnected);
    }

    [Fact]
    public async Task ThreeNodesDiscoverEachOtherThroughGossip()
    {
        var seed = StartNode();
        var nodeB = StartNode(c => c.Bootstrap.Add(AddressOf(seed)));
        await WaitUntil(() => ConnectedTo(nodeB, seed));
        var nodeC = StartNode(c => c.Bootstrap.Add(AddressOf(seed)));

        // B y C solo conocen al nodo de arranque; se encuentran entre ellos por intercambio de direcciones.
        await WaitUntil(() => ConnectedTo(nodeB, nodeC) && ConnectedTo(nodeC, nodeB), 40, "B y C conectados");

        // Sin duplicados: un único enlace por par de nodos.
        Assert.Equal(1, nodeB.P2P.GetPeers().Count(p => p.HandshakeComplete && p.PeerId == nodeC.P2P.PeerId.ToString()));

        // La red sigue funcionando sin el nodo de arranque.
        await Stop(seed);
        using var miner = TestChain.NewKey();
        nodeB.Chain.ProcessBlock(MineBlock(nodeB, TestChain.AddressOf(miner)));
        await WaitUntil(() => nodeC.Chain.Height == 1, 20, "bloque propagado sin el nodo de arranque");
    }

    [Fact]
    public async Task TransactionsAndBlocksPropagateAcrossHops()
    {
        using var alice = TestChain.NewKey();
        using var bob = TestChain.NewKey();
        var a = TestChain.AddressOf(alice);

        // Topología en línea A ← B ← C: A acepta una sola conexión entrante, así C no puede llegar directo.
        var nodeA = StartNode(c => c.MaxInbound = 1);
        for (var i = 0; i < 3; i++) nodeA.Chain.ProcessBlock(MineBlock(nodeA, a));
        var nodeB = StartNode(c => { c.Bootstrap.Add(AddressOf(nodeA)); c.MaxOutbound = 1; });
        await WaitUntil(() => ConnectedTo(nodeB, nodeA));
        var nodeC = StartNode(c => { c.Bootstrap.Add(AddressOf(nodeB)); c.MaxOutbound = 1; });

        await WaitUntil(() => nodeC.Chain.Height == 3, 30, "C sincroniza a través de B");
        Assert.False(ConnectedTo(nodeC, nodeA));

        // Bloque minado en A llega a C pasando por B.
        nodeA.Chain.ProcessBlock(MineBlock(nodeA, a));
        await WaitUntil(() => nodeC.Chain.Height == 4, 20, "bloque nuevo en C");

        // Transacción creada en C llega al mempool de A pasando por B, y vuelve confirmada.
        var tx = Transaction.CreateTransfer(alice, nodeC.Network.Magic, TestChain.AddressOf(bob), 3 * Money.Coin, 2000, 0);
        Assert.True(nodeC.SubmitTransaction(tx).Accepted);
        await WaitUntil(() => nodeA.Mempool.Contains(tx.Id), 20, "transacción en A");
        nodeA.Chain.ProcessBlock(MineBlock(nodeA, a));
        await WaitUntil(() => nodeC.Chain.GetAccount(TestChain.AddressOf(bob)).Balance == 3 * Money.Coin, 20);
        Assert.Equal(0, nodeC.Mempool.Count);
        Assert.True(nodeC.Chain.Reconcile().Consistent);
    }

    [Fact]
    public async Task EmptyNodeSyncsLongChainInBatches()
    {
        using var miner = TestChain.NewKey();
        var nodeA = StartNode();
        for (var i = 0; i < 160; i++) nodeA.Chain.ProcessBlock(MineBlock(nodeA, TestChain.AddressOf(miner)));
        Assert.Equal(160, nodeA.Chain.Height);

        var nodeB = StartNode(c => c.Bootstrap.Add(AddressOf(nodeA)));
        await WaitUntil(() => nodeB.Chain.Height == 160, 60, "sincronización completa");
        Assert.Equal(nodeA.Chain.Tip.Hash, nodeB.Chain.Tip.Hash);
        Assert.Equal(nodeA.Chain.IssuedSupply, nodeB.Chain.IssuedSupply);
        Assert.True(nodeB.Chain.Reconcile().Consistent);
        await WaitUntil(() => !nodeB.GetStatus().Syncing, 10);
    }

    [Fact]
    public async Task NodeReconnectsAndRecoversAfterRestart()
    {
        using var miner = TestChain.NewKey();
        var configA = NewConfig();
        var nodeA = Start(configA);
        for (var i = 0; i < 5; i++) nodeA.Chain.ProcessBlock(MineBlock(nodeA, TestChain.AddressOf(miner)));
        var peerIdA = nodeA.P2P.PeerId;
        var nodeB = StartNode(c => c.Bootstrap.Add(AddressOf(nodeA)));
        await WaitUntil(() => nodeB.Chain.Height == 5 && ConnectedTo(nodeA, nodeB));

        // A se apaga: B detecta la desconexión.
        await Stop(nodeA);
        await WaitUntil(() => nodeB.GetStatus().PeersConnected == 0, 15, "B detecta la caída");

        // B mina mientras A está apagado.
        nodeB.Chain.ProcessBlock(MineBlock(nodeB, TestChain.AddressOf(miner)));

        // A reinicia con la misma carpeta y puerto: conserva cadena e identidad, y B se reconecta solo.
        var restarted = Start(configA);
        Assert.Equal(peerIdA, restarted.P2P.PeerId);
        Assert.Equal(5, restarted.Chain.Height);
        await WaitUntil(() => ConnectedTo(nodeB, restarted), 40, "reconexión automática");
        await WaitUntil(() => restarted.Chain.Height == 6, 20, "A recupera el bloque minado durante la caída");
        Assert.True(restarted.Chain.Reconcile().Consistent);
    }

    // =====================================================================
    // Pares maliciosos
    // =====================================================================

    /// <summary>Cliente P2P mínimo que hace el handshake real y luego envía lo que la prueba quiera.</summary>
    private sealed class RawPeer : IDisposable
    {
        private readonly TcpClient _client;
        private readonly NetworkParams _network;
        public NetworkStream Stream { get; }
        public NodeIdentity Identity { get; }
        public HelloMessage? RemoteHello { get; private set; }

        private RawPeer(TcpClient client, NetworkParams network, NodeIdentity identity)
        {
            _client = client;
            _network = network;
            Stream = client.GetStream();
            Identity = identity;
        }

        public static async Task<RawPeer> ConnectAsync(ShentNode node, BigInteger? claimedWork = null, long claimedHeight = 0, bool corruptSignature = false)
        {
            var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, node.P2P.ListeningPort);
            var peer = new RawPeer(client, node.Network, NodeIdentity.Ephemeral());
            var challenge = Hash256.Sha256(Guid.NewGuid().ToByteArray());
            await peer.SendAsync(new HelloMessage(Protocol.Version, node.Network.Magic, node.Chain.GenesisHash, claimedHeight,
                claimedWork ?? BigInteger.One, Hash256.Zero, 0, NodeServices.None, peer.Identity.PeerId, challenge,
                IPAddress.Loopback, node.P2P.ListeningPort, "raw-test", DateTimeOffset.UtcNow.ToUnixTimeSeconds()));
            var hello = (HelloMessage)await peer.ReceiveAsync(m => m is HelloMessage);
            peer.RemoteHello = hello;
            var signature = peer.Identity.SignHandshake(node.Network.Magic, hello.Challenge, hello.NodeId);
            if (corruptSignature) signature[0] ^= 0xFF;
            await peer.SendAsync(new HelloAckMessage(signature));
            if (!corruptSignature)
            {
                var ack = (HelloAckMessage)await peer.ReceiveAsync(m => m is HelloAckMessage);
                Assert.True(NodeIdentity.VerifyHandshake(node.Network.Magic, challenge, hello.NodeId, peer.Identity.PeerId, ack.Signature));
            }
            return peer;
        }

        public Task SendAsync(Message message) => FrameIO.WriteAsync(Stream, _network, message, CancellationToken.None);

        public async Task<Message> ReceiveAsync(Func<Message, bool> match, int seconds = 10)
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));
            while (true)
            {
                var (type, payload) = await FrameIO.ReadAsync(Stream, _network, Protocol.MaxPayload(_network), cts.Token);
                var message = Protocol.Decode(type, payload);
                if (match(message)) return message;
            }
        }

        public async Task<int> CountAsync(Func<Message, bool> match, TimeSpan window)
        {
            var count = 0;
            using var cts = new CancellationTokenSource(window);
            try
            {
                while (true)
                {
                    var (type, payload) = await FrameIO.ReadAsync(Stream, _network, Protocol.MaxPayload(_network), cts.Token);
                    if (match(Protocol.Decode(type, payload))) count++;
                }
            }
            catch (OperationCanceledException) { }
            catch (IOException) { }
            return count;
        }

        public void Dispose()
        {
            _client.Dispose();
            Identity.Dispose();
        }
    }

    private static async Task<bool> IsClosedByRemote(NetworkStream stream)
    {
        var buffer = new byte[4096];
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            while (true)
            {
                if (await stream.ReadAsync(buffer, timeout.Token) == 0) return true;
            }
        }
        catch (IOException)
        {
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    [Fact]
    public async Task InvalidBlockFromAuthenticatedPeerIsRejectedAndPeerBanned()
    {
        using var miner = TestChain.NewKey();
        using var thief = TestChain.NewKey();
        var node = StartNode();
        node.Chain.ProcessBlock(MineBlock(node, TestChain.AddressOf(miner)));
        var supply = node.Chain.IssuedSupply;

        using var raw = await RawPeer.ConnectAsync(node);
        // Bloque con PoW válido pero con una coinbase que se paga 1000 SHENT a sí misma: el par está
        // autenticado, pero eso no lo hace confiable.
        var valid = MineBlock(node, TestChain.AddressOf(thief));
        var forged = Transaction.CreateCoinbase(node.Network.Magic, valid.Header.Height, TestChain.AddressOf(thief), 1000 * Money.Coin, []);
        var invalid = new Block { Header = valid.Header, Transactions = [forged] };
        await raw.SendAsync(new BlockMessage(invalid.Serialize()));

        Assert.True(await IsClosedByRemote(raw.Stream));
        Assert.Equal(1, node.Chain.Height);
        Assert.Equal(supply, node.Chain.IssuedSupply);
        Assert.Equal(0, node.Chain.GetAccount(TestChain.AddressOf(thief)).Balance);
        await WaitUntil(() => node.P2P.AddressBook.IsBanned(raw.Identity.PeerId), 5);
    }

    [Fact]
    public async Task OversizedFramesAndBadSignaturesAreRejectedBeforeHandshake()
    {
        var node = StartNode();
        var port = node.P2P.ListeningPort;

        // Trama que anuncia 1 MiB antes del handshake (límite: 8 KiB).
        using (var client = new TcpClient())
        {
            await client.ConnectAsync(IPAddress.Loopback, port);
            var stream = client.GetStream();
            var header = new byte[Protocol.FrameHeaderSize];
            BinaryPrimitives.WriteUInt32LittleEndian(header, node.Network.Magic);
            header[4] = (byte)MessageType.Hello;
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(5), 1024 * 1024);
            await stream.WriteAsync(header);
            Assert.True(await IsClosedByRemote(stream));
        }

        // Basura aleatoria.
        using (var client = new TcpClient())
        {
            await client.ConnectAsync(IPAddress.Loopback, port);
            var stream = client.GetStream();
            await stream.WriteAsync(System.Security.Cryptography.RandomNumberGenerator.GetBytes(64));
            Assert.True(await IsClosedByRemote(stream));
        }

        // Firma de identidad inválida.
        using (var raw = await RawPeer.ConnectAsync(node, corruptSignature: true))
        {
            Assert.True(await IsClosedByRemote(raw.Stream));
        }

        // Mensaje malformado después del handshake.
        using (var raw = await RawPeer.ConnectAsync(node))
        {
            var frame = Protocol.Frame(node.Network, new InvMessage(InvType.Block, []));
            frame[Protocol.FrameHeaderSize] = 0x77; // tipo de inventario inexistente
            var checksum = System.Security.Cryptography.SHA256.HashData(frame.AsSpan(Protocol.FrameHeaderSize));
            checksum.AsSpan(0, 4).CopyTo(frame.AsSpan(9));
            await raw.Stream.WriteAsync(frame);
            Assert.True(await IsClosedByRemote(raw.Stream));
        }

        Assert.Equal(0, node.GetStatus().PeersConnected);
    }

    [Fact]
    public async Task PeerThatLiesAboutItsWorkDoesNotCauseSyncLoop()
    {
        var node = StartNode();
        var huge = BigInteger.Pow(2, 200);
        using var liar = await RawPeer.ConnectAsync(node, claimedWork: huge, claimedHeight: 1_000_000);

        // El nodo intenta sincronizar, el mentiroso no tiene bloques: tras la primera respuesta vacía no
        // vuelve a pedirle en bucle, y la altura anunciada se corrige a lo verificado.
        await liar.ReceiveAsync(m => m is GetBlocksMessage);
        await liar.SendAsync(new InvMessage(InvType.Block, []));
        var repeated = await liar.CountAsync(m => m is GetBlocksMessage, TimeSpan.FromSeconds(6));
        Assert.True(repeated <= 1, $"GET_BLOCKS repetidos: {repeated}");
        Assert.Equal(0, node.GetStatus().BestKnownHeight);
        Assert.False(node.GetStatus().Syncing);
    }

    // =====================================================================
    // NAT: alcanzabilidad y relay
    // =====================================================================

    [Fact]
    public async Task DialBackDetectsReachableAndUnreachableNodes()
    {
        var nodeA = StartNode();
        var nodeB = StartNode(c => c.Bootstrap.Add(AddressOf(nodeA)));
        var hidden = StartNode(c => { c.Listen = false; c.Bootstrap.Add(AddressOf(nodeA)); });

        await WaitUntil(() => nodeB.P2P.Reachability == Reachability.Public && nodeA.P2P.Reachability == Reachability.Public, 30, "dial-back");
        Assert.Equal(Reachability.Private, hidden.P2P.Reachability);
        Assert.NotNull(nodeB.P2P.PublicAddress);
        Assert.Contains(nodeB.P2P.GetPublicAddresses(), a => a.EndsWith($"/p2p/{nodeB.P2P.PeerId}"));
    }

    [Fact]
    public async Task RelayCircuitLetsNodesReachPeerWithoutOpenPorts()
    {
        using var miner = TestChain.NewKey();
        using var bob = TestChain.NewKey();
        var relay = StartNode(c => c.RelayService = true);
        var hidden = StartNode(c => { c.Listen = false; c.Bootstrap.Add(AddressOf(relay)); });

        await WaitUntil(() => hidden.P2P.GetRelayReservations().Count == 1, 30, "reserva en el relay");
        var circuit = hidden.P2P.GetRelayReservations()[0].CircuitAddress;
        Assert.Contains("/p2p-circuit/p2p/", circuit);
        Assert.Contains(circuit, hidden.P2P.GetPublicAddresses());

        // Un tercer nodo llega al nodo oculto solo con la dirección de circuito.
        var dialer = StartNode(c => c.MaxOutbound = 1);
        Assert.True(await dialer.P2P.AddPeerAsync(circuit));
        await WaitUntil(() => ConnectedTo(dialer, hidden) && ConnectedTo(hidden, dialer), 30, "circuito establecido");
        Assert.True(dialer.P2P.GetPeers().Single(p => p.PeerId == hidden.P2P.PeerId.ToString()).Relayed);
        Assert.Equal(1, relay.P2P.Relay!.ActiveCircuits);

        // Bloques y transacciones viajan por el circuito y se validan en cada extremo.
        for (var i = 0; i < 2; i++) dialer.Chain.ProcessBlock(MineBlock(dialer, TestChain.AddressOf(miner)));
        await WaitUntil(() => hidden.Chain.Height == 2, 20, "bloques por el circuito");
        var tx = Transaction.CreateTransfer(miner, hidden.Network.Magic, TestChain.AddressOf(bob), Money.Coin, 2000, 0);
        Assert.True(hidden.SubmitTransaction(tx).Accepted);
        await WaitUntil(() => dialer.Mempool.Contains(tx.Id), 20, "transacción por el circuito");
    }

    [Fact]
    public async Task RelayRefusesCircuitsToUnknownTargets()
    {
        var relay = StartNode(c => c.RelayService = true);
        using var nobody = NodeIdentity.Ephemeral();
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, relay.P2P.ListeningPort);
        var stream = client.GetStream();
        await FrameIO.WriteAsync(stream, relay.Network, new CircuitConnectMessage(nobody.PeerId), CancellationToken.None);
        var reply = await FrameIO.ReadMessageAsync(stream, relay.Network, TimeSpan.FromSeconds(5), CancellationToken.None);
        Assert.Equal(new CircuitStatusMessage(RelayStatus.NoReservation), reply);

        // Un nodo sin relay activado responde que no ofrece el servicio.
        var plain = StartNode();
        using var client2 = new TcpClient();
        await client2.ConnectAsync(IPAddress.Loopback, plain.P2P.ListeningPort);
        await FrameIO.WriteAsync(client2.GetStream(), plain.Network, new CircuitConnectMessage(nobody.PeerId), CancellationToken.None);
        var reply2 = await FrameIO.ReadMessageAsync(client2.GetStream(), plain.Network, TimeSpan.FromSeconds(5), CancellationToken.None);
        Assert.Equal(new CircuitStatusMessage(RelayStatus.NotRelay), reply2);
    }

    // =====================================================================
    // API local
    // =====================================================================

    [Fact]
    public async Task LocalApiReportsStatusPeersAndInfoWithoutSecrets()
    {
        var apiPort = FreePort();
        var nodeA = StartNode(c => { c.Api = true; c.ApiPort = apiPort; });
        var nodeB = StartNode(c => c.Bootstrap.Add(AddressOf(nodeA)));
        await WaitUntil(() => ConnectedTo(nodeA, nodeB));

        using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{apiPort}") };
        using var status = JsonDocument.Parse(await http.GetStringAsync("/api/p2p/status"));
        Assert.Equal(nodeA.P2P.PeerId.ToString(), status.RootElement.GetProperty("peerId").GetString());
        Assert.Equal(1, status.RootElement.GetProperty("peers").GetProperty("connected").GetInt32());
        Assert.Equal(0, status.RootElement.GetProperty("height").GetInt64());
        Assert.False(status.RootElement.GetProperty("syncing").GetBoolean());

        using var peers = JsonDocument.Parse(await http.GetStringAsync("/api/p2p/peers"));
        Assert.Equal(nodeB.P2P.PeerId.ToString(), peers.RootElement[0].GetProperty("peerId").GetString());

        var infoText = await http.GetStringAsync("/api/p2p/info");
        using var info = JsonDocument.Parse(infoText);
        Assert.Equal(apiPort, info.RootElement.GetProperty("apiPort").GetInt32());
        Assert.Contains(info.RootElement.GetProperty("localAddresses").EnumerateArray(), a => a.GetString()!.Contains(nodeA.P2P.PeerId.ToString()));

        // Nada secreto: ni la semilla de la identidad del nodo ni claves.
        var keyFile = File.ReadAllText(ShentNode.IdentityPath(nodeA.Config));
        var seedHex = JsonDocument.Parse(keyFile).RootElement.GetProperty("Seed").GetString()!;
        foreach (var body in new[] { infoText, status.RootElement.GetRawText(), peers.RootElement.GetRawText() })
        {
            Assert.DoesNotContain(seedHex, body, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("private", body, StringComparison.OrdinalIgnoreCase);
        }

        // Solo lectura y solo para localhost.
        var post = await http.PostAsync("/api/p2p/status", new StringContent(""));
        Assert.Equal(HttpStatusCode.MethodNotAllowed, post.StatusCode);
        using var rebinding = new HttpRequestMessage(HttpMethod.Get, "/api/p2p/status");
        rebinding.Headers.Host = "malicioso.example";
        Assert.Equal(HttpStatusCode.Forbidden, (await http.SendAsync(rebinding)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync("/api/wallet")).StatusCode);
        Assert.True(DiagnosticsServer.IsLocalHost("localhost:37380"));
        Assert.False(DiagnosticsServer.IsLocalHost("192.168.0.5:37380"));
    }

    [Fact]
    public async Task LocalApiStartsLaterIfItsPortWasBusy()
    {
        var apiPort = FreePort();
        var previous = ShentNode.ApiRetryInterval;
        ShentNode.ApiRetryInterval = TimeSpan.FromMilliseconds(200);
        try
        {
            var blocker = new TcpListener(IPAddress.Loopback, apiPort);
            blocker.Start();
            var node = StartNode(c => { c.Api = true; c.ApiPort = apiPort; });
            await Task.Delay(500);
            Assert.Null(node.Api);

            blocker.Stop();
            await WaitUntil(() => node.Api is not null);
            using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{apiPort}") };
            using var status = JsonDocument.Parse(await http.GetStringAsync("/api/p2p/status"));
            Assert.Equal(node.P2P.PeerId.ToString(), status.RootElement.GetProperty("peerId").GetString());
        }
        finally
        {
            ShentNode.ApiRetryInterval = previous;
        }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var node in _nodes) await node.DisposeAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var dir in _dirs)
        {
            try { Directory.Delete(dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}
