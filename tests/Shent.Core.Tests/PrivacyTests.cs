using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Numerics;
using System.Text.Json;
using Shent.Core.Chain;
using Shent.Core.Consensus;
using Shent.Core.Crypto;
using Shent.Core.Mempool;
using Shent.Core.Mining;
using Shent.Core.Model;
using Shent.Core.Node;
using Shent.Core.P2P;

namespace Shent.Core.Tests;

/// <summary>Dandelion++, conexión por proxy SOCKS5 (Tor) y transacciones privadas en el mempool.</summary>
public sealed class PrivacyTests : IAsyncDisposable
{
    private readonly List<ShentNode> _nodes = [];
    private readonly List<string> _dirs = [];
    private readonly List<IDisposable> _disposables = [];

    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    private ShentNode StartNode(Action<NodeConfig>? customize = null)
    {
        var dir = Path.Combine(Path.GetTempPath(), "shent-privacy-tests", Guid.NewGuid().ToString("N"));
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
        var node = ShentNode.Start(config);
        _nodes.Add(node);
        return node;
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
    // Mempool: transacciones en fase privada
    // =====================================================================

    [Fact]
    public void PrivateTransactionsAreNeitherAnnouncedNorServedNorMined()
    {
        using var t = new TestChain();
        using var alice = TestChain.NewKey();
        using var bob = TestChain.NewKey();
        t.MineMany(TestChain.AddressOf(alice), 2);
        var pool = new TxMempool(t.Chain);
        var b = TestChain.AddressOf(bob);

        var first = Transaction.CreateTransfer(alice, t.Network.Magic, b, Money.Coin, 2000, 0);
        var second = Transaction.CreateTransfer(alice, t.Network.Magic, b, Money.Coin, 2000, 1);
        Assert.True(pool.TryAdd(first, isPrivate: true).Accepted);
        Assert.True(pool.TryAdd(second).Accepted);

        Assert.True(pool.Contains(first.Id));
        Assert.True(pool.IsPrivate(first.Id));
        Assert.Null(pool.Get(first.Id));
        Assert.DoesNotContain(pool.GetAll(), x => x.Id == first.Id);
        Assert.Contains(pool.GetAllIncludingPrivate(), x => x.Id == first.Id);
        Assert.Equal(2, pool.NextNonce(TestChain.AddressOf(alice)));
        // La privada corta la cola del remitente: la siguiente depende de su nonce y tampoco se mina.
        Assert.Empty(pool.SelectForBlock(100_000));

        Assert.True(pool.MakePublic(first.Id));
        Assert.False(pool.MakePublic(first.Id));
        Assert.Equal(2, pool.SelectForBlock(100_000).Count);
        Assert.Same(first, pool.Get(first.Id));
    }

    // =====================================================================
    // Dandelion++
    // =====================================================================

    [Fact]
    public async Task OwnTransactionTravelsByStemAndStaysHiddenUntilFluffed()
    {
        using var miner = TestChain.NewKey();
        using var bob = TestChain.NewKey();
        var node = StartNode();
        node.Chain.ProcessBlock(MineBlock(node, TestChain.AddressOf(miner)));
        using var raw = await RawPeer.ConnectAsync(node, Protocol.Version);
        await WaitUntil(() => node.P2P.GetPeers().Any(p => p.HandshakeComplete), 10, "handshake");

        var tx = Transaction.CreateTransfer(miner, node.Network.Magic, TestChain.AddressOf(bob), Money.Coin, 2000, 0);
        Assert.True(node.SubmitTransaction(tx).Accepted);

        var stem = (StemTxMessage)await raw.ReceiveAsync(m => m is StemTxMessage or InvMessage { Kind: InvType.Tx });
        Assert.Equal(tx.Id, Transaction.Deserialize(stem.Payload).Id);
        Assert.True(node.Mempool.IsPrivate(tx.Id));

        // Mientras está en el tallo, el nodo responde como si no la tuviera.
        await raw.SendAsync(new GetDataMessage(InvType.Tx, [tx.Id]));
        var notFound = (NotFoundMessage)await raw.ReceiveAsync(m => m is NotFoundMessage or TxMessage);
        Assert.Contains(tx.Id, notFound.Hashes);

        // Alguien la anuncia difundida: termina la fase privada.
        await raw.SendAsync(new InvMessage(InvType.Tx, [tx.Id]));
        await WaitUntil(() => !node.Mempool.IsPrivate(tx.Id), 10, "difusión");
        Assert.NotNull(node.Mempool.Get(tx.Id));
    }

    [Fact]
    public async Task LegacyPeersNeverReceiveStemMessages()
    {
        using var miner = TestChain.NewKey();
        using var bob = TestChain.NewKey();
        var node = StartNode();
        node.Chain.ProcessBlock(MineBlock(node, TestChain.AddressOf(miner)));
        using var legacy = await RawPeer.ConnectAsync(node, protocolVersion: 2);
        await WaitUntil(() => node.P2P.GetPeers().Any(p => p.HandshakeComplete), 10, "handshake");

        var tx = Transaction.CreateTransfer(miner, node.Network.Magic, TestChain.AddressOf(bob), Money.Coin, 2000, 0);
        Assert.True(node.SubmitTransaction(tx).Accepted);
        var first = await legacy.ReceiveAsync(m => m is StemTxMessage or InvMessage { Kind: InvType.Tx });
        Assert.IsType<InvMessage>(first);
        Assert.False(node.Mempool.IsPrivate(tx.Id));
    }

    [Fact]
    public async Task InvalidStemTransactionIsRejectedAndPunished()
    {
        using var miner = TestChain.NewKey();
        using var bob = TestChain.NewKey();
        var node = StartNode();
        node.Chain.ProcessBlock(MineBlock(node, TestChain.AddressOf(miner)));
        using var raw = await RawPeer.ConnectAsync(node, Protocol.Version);
        await WaitUntil(() => node.P2P.GetPeers().Any(p => p.HandshakeComplete), 10, "handshake");

        var forged = Transaction.CreateTransfer(miner, node.Network.Magic, TestChain.AddressOf(bob), Money.Coin, 2000, 0);
        forged.Signature[0] ^= 0xFF;
        await raw.SendAsync(new StemTxMessage(forged.Serialize()));
        await WaitUntil(() => node.P2P.GetPeers().Any(p => p.Misbehavior >= 20) || !node.P2P.GetPeers().Any(), 10, "penalización");
        Assert.False(node.Mempool.Contains(forged.Id));
    }

    [Fact]
    public async Task StemmedTransactionReachesEveryNodeAndGetsMined()
    {
        using var miner = TestChain.NewKey();
        using var bob = TestChain.NewKey();
        var a = StartNode();
        var b = StartNode(c => c.Bootstrap.Add(AddressOf(a)));
        var c = StartNode(c2 => c2.Bootstrap.Add(AddressOf(b)));
        await WaitUntil(() => ConnectedTo(b, a) && ConnectedTo(c, b), 30, "cadena de nodos");

        a.Chain.ProcessBlock(MineBlock(a, TestChain.AddressOf(miner)));
        await WaitUntil(() => b.Chain.Height == 1 && c.Chain.Height == 1, 20, "bloque propagado");

        var tx = Transaction.CreateTransfer(miner, c.Network.Magic, TestChain.AddressOf(bob), 3 * Money.Coin, 2000, 0);
        Assert.True(c.SubmitTransaction(tx).Accepted);
        await WaitUntil(() => new[] { a, b, c }.All(n => n.Mempool.Contains(tx.Id) && !n.Mempool.IsPrivate(tx.Id)), 90, "difusión en los tres nodos");

        var block = MineBlock(a, TestChain.AddressOf(miner));
        Assert.Contains(block.Transactions, t => t.Id == tx.Id);
        a.Chain.ProcessBlock(block);
        await WaitUntil(() => c.Chain.GetAccount(TestChain.AddressOf(bob)).Balance == 3 * Money.Coin, 20, "confirmación");
    }

    // =====================================================================
    // Proxy SOCKS5 (Tor)
    // =====================================================================

    [Fact]
    public async Task ProxyModeConnectsThroughSocks5AndHidesEverythingThatRevealsTheIp()
    {
        var a = StartNode();
        var proxy = new FakeSocks5Proxy();
        _disposables.Add(proxy);
        var hidden = StartNode(c =>
        {
            c.Proxy = $"127.0.0.1:{proxy.Port}";
            c.Bootstrap.Add(AddressOf(a));
            c.Dht = true;
            c.DhtRouters.Add("127.0.0.1:9");
            c.LanDiscovery = true;
            c.Upnp = true;
        });

        await WaitUntil(() => ConnectedTo(hidden, a) && ConnectedTo(a, hidden), 30, "conexión por el proxy");
        Assert.Contains(new IPEndPoint(IPAddress.Loopback, a.P2P.ListeningPort), proxy.Targets);
        Assert.NotEmpty(proxy.Usernames);
        Assert.Equal(proxy.Usernames.Count, proxy.Usernames.Distinct().Count());

        Assert.True(hidden.P2P.ProxyMode);
        Assert.False(hidden.P2P.IsListening);
        Assert.False(hidden.P2P.Dht.Running);
        Assert.False(hidden.P2P.LanDiscoveryActive);
        Assert.Equal(UpnpState.Disabled, hidden.P2P.UpnpState);
        // El par se muestra con su dirección real, no con la del proxy.
        Assert.Contains(hidden.P2P.GetPeers(), p => p.Address == $"127.0.0.1:{a.P2P.ListeningPort}");
        // Del otro lado no se anuncia ningún puerto propio del nodo oculto.
        Assert.DoesNotContain(a.P2P.GetPeers(), p => p.PeerId == hidden.P2P.PeerId.ToString() && p.Address.EndsWith($":{hidden.Config.EffectivePort}"));
    }

    [Fact]
    public async Task InvalidProxyFailsClosedInsteadOfConnectingDirectly()
    {
        var a = StartNode();
        var hidden = StartNode(c =>
        {
            c.Proxy = "esto-no-es-un-proxy";
            c.Bootstrap.Add(AddressOf(a));
        });
        await Task.Delay(4000);
        Assert.DoesNotContain(hidden.P2P.GetPeers(), p => p.HandshakeComplete);
        Assert.DoesNotContain(a.P2P.GetPeers(), p => p.HandshakeComplete);
        Assert.False(hidden.P2P.IsListening);
    }

    [Fact]
    public async Task HostnamesAreResolvedThroughTorInsteadOfTheLocalDns()
    {
        using var proxy = new FakeSocks5Proxy();
        var ips = await Socks5Client.ResolveAsync(new IPEndPoint(IPAddress.Loopback, proxy.Port), "semilla.ejemplo.invalid", CancellationToken.None);
        Assert.Equal([IPAddress.Parse("10.1.2.3")], ips);
        Assert.Contains("semilla.ejemplo.invalid", proxy.Resolved);

        Assert.True(P2PNetwork.TryParseProxy("127.0.0.1:9050", out var ep));
        Assert.Equal(9050, ep.Port);
        Assert.True(P2PNetwork.TryParseProxy("localhost:9150", out var local));
        Assert.Equal(IPAddress.Loopback, local.Address);
        Assert.False(P2PNetwork.TryParseProxy("127.0.0.1", out _));
        Assert.False(P2PNetwork.TryParseProxy("tor", out _));
    }

    // =====================================================================
    // Puntos de control
    // =====================================================================

    [Fact]
    public void CheckpointFileIsParsedStrictly()
    {
        var hash = Hash256.Sha256("x"u8).ToString();
        var parsed = CheckpointData.Parse(JsonDocument.Parse($$"""{ "mainnet": { "1000": "{{hash}}" }, "testnet": {} }"""));
        Assert.Equal(hash, parsed["mainnet"][1000].ToString());
        Assert.Empty(parsed["testnet"]);
        Assert.Throws<FormatException>(() => CheckpointData.Parse(JsonDocument.Parse("""{ "mainnet": { "0": "00" } }""")));
        Assert.Throws<FormatException>(() => CheckpointData.Parse(JsonDocument.Parse("""{ "mainnet": { "5": "no-es-hash" } }""")));
        Assert.NotNull(NetworkParams.Mainnet.Checkpoints);
    }

    [Fact]
    public void ChainRejectsHistoryThatContradictsCheckpoints()
    {
        using var miner = TestChain.NewKey();
        using var other = TestChain.NewKey();
        using var source = new TestChain();
        source.MineMany(TestChain.AddressOf(miner), 5);
        var blocks = Enumerable.Range(1, 5).Select(h => source.Chain.GetBlockAtHeight(h)!).ToList();
        var network = NetworkParams.Regtest with { Checkpoints = new Dictionary<long, Hash256> { [3] = blocks[2].Hash } };

        // Antes de llegar al punto de control: un bloque distinto en esa altura se rechaza; el correcto se acepta.
        using (var early = new TestChain(network))
        {
            Assert.Equal(BlockAcceptStatus.NewTip, early.Chain.ProcessBlock(blocks[0]).Status);
            Assert.Equal(BlockAcceptStatus.NewTip, early.Chain.ProcessBlock(blocks[1]).Status);
            var impostor = early.BuildBlock(early.Chain.Tip, TestChain.AddressOf(other));
            var result = early.Chain.ProcessBlock(impostor);
            Assert.Equal(BlockAcceptStatus.Invalid, result.Status);
            Assert.Contains("punto de control", result.Reason);
            Assert.Equal(BlockAcceptStatus.NewTip, early.Chain.ProcessBlock(blocks[2]).Status);
        }

        // Pasado el punto de control: ya no se aceptan ramas que se separen antes de él.
        using var synced = new TestChain(network);
        foreach (var block in blocks) Assert.Equal(BlockAcceptStatus.NewTip, synced.Chain.ProcessBlock(block).Status);
        var fork = synced.BuildBlock(IndexOf(blocks[0]), TestChain.AddressOf(other), timestamp: blocks[0].Header.Timestamp + 61);
        var forkResult = synced.Chain.ProcessBlock(fork);
        Assert.Equal(BlockAcceptStatus.Invalid, forkResult.Status);
        Assert.Contains("punto de control", forkResult.Reason);
        Assert.Equal(5, synced.Chain.Height);
    }

    [Fact]
    public void CheckpointCandidatesOnlyIncludeDeeplyBuriedBlocks()
    {
        var node = StartNode();
        using var miner = TestChain.NewKey();
        for (var i = 0; i < 3; i++) node.Chain.ProcessBlock(MineBlock(node, TestChain.AddressOf(miner)));
        var json = JsonSerializer.Serialize(node.GetCheckpointCandidates());
        using var doc = JsonDocument.Parse(json);
        Assert.Equal("regtest", doc.RootElement.GetProperty("network").GetString());
        Assert.Equal(3, doc.RootElement.GetProperty("height").GetInt64());
        Assert.Equal(0, doc.RootElement.GetProperty("checkpoints").GetArrayLength());
    }

    private static BlockIndex IndexOf(Block block) => new()
    {
        Hash = block.Hash,
        Height = block.Header.Height,
        Prev = null,
        Timestamp = block.Header.Timestamp,
        Bits = block.Header.Bits,
        TotalWork = 0,
    };

    // =====================================================================
    // Utilidades
    // =====================================================================

    /// <summary>Cliente P2P mínimo con versión de protocolo configurable.</summary>
    private sealed class RawPeer : IDisposable
    {
        private readonly TcpClient _client;
        private readonly NetworkParams _network;
        private NetworkStream Stream { get; }
        private NodeIdentity Identity { get; }

        private RawPeer(TcpClient client, NetworkParams network, NodeIdentity identity)
        {
            _client = client;
            _network = network;
            Stream = client.GetStream();
            Identity = identity;
        }

        public static async Task<RawPeer> ConnectAsync(ShentNode node, uint protocolVersion)
        {
            var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, node.P2P.ListeningPort);
            var peer = new RawPeer(client, node.Network, NodeIdentity.Ephemeral());
            var challenge = Hash256.Sha256(Guid.NewGuid().ToByteArray());
            await peer.SendAsync(new HelloMessage(protocolVersion, node.Network.Magic, node.Chain.GenesisHash, 0,
                BigInteger.One, Hash256.Zero, 0, NodeServices.None, peer.Identity.PeerId, challenge,
                IPAddress.Loopback, node.P2P.ListeningPort, "raw-test", DateTimeOffset.UtcNow.ToUnixTimeSeconds()));
            var hello = (HelloMessage)await peer.ReceiveAsync(m => m is HelloMessage);
            await peer.SendAsync(new HelloAckMessage(peer.Identity.SignHandshake(node.Network.Magic, hello.Challenge, hello.NodeId)));
            await peer.ReceiveAsync(m => m is HelloAckMessage);
            return peer;
        }

        public Task SendAsync(Message message) => FrameIO.WriteAsync(Stream, _network, message, CancellationToken.None);

        public async Task<Message> ReceiveAsync(Func<Message, bool> match, int seconds = 15)
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));
            while (true)
            {
                var (type, payload) = await FrameIO.ReadAsync(Stream, _network, Protocol.MaxPayload(_network), cts.Token);
                var message = Protocol.Decode(type, payload);
                if (match(message)) return message;
            }
        }

        public void Dispose()
        {
            _client.Dispose();
            Identity.Dispose();
        }
    }

    /// <summary>Proxy SOCKS5 de prueba: usuario/contraseña, CONNECT y la extensión RESOLVE de Tor.</summary>
    private sealed class FakeSocks5Proxy : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _cts = new();
        public ConcurrentBag<IPEndPoint> Targets { get; } = [];
        public ConcurrentBag<string> Usernames { get; } = [];
        public ConcurrentBag<string> Resolved { get; } = [];
        public int Port { get; }

        public FakeSocks5Proxy()
        {
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _ = Task.Run(AcceptLoop);
        }

        private async Task AcceptLoop()
        {
            while (!_cts.IsCancellationRequested)
            {
                TcpClient client;
                try { client = await _listener.AcceptTcpClientAsync(_cts.Token); }
                catch (Exception) { return; }
                _ = Task.Run(() => Handle(client));
            }
        }

        private async Task Handle(TcpClient client)
        {
            using var _ = client;
            try
            {
                var s = client.GetStream();
                var head = new byte[2];
                await s.ReadExactlyAsync(head);
                var methods = new byte[head[1]];
                await s.ReadExactlyAsync(methods);
                if (methods.Contains((byte)2))
                {
                    await s.WriteAsync(new byte[] { 5, 2 });
                    var ver = new byte[2];
                    await s.ReadExactlyAsync(ver);
                    var user = new byte[ver[1]];
                    await s.ReadExactlyAsync(user);
                    var plen = new byte[1];
                    await s.ReadExactlyAsync(plen);
                    await s.ReadExactlyAsync(new byte[plen[0]]);
                    Usernames.Add(System.Text.Encoding.ASCII.GetString(user));
                    await s.WriteAsync(new byte[] { 1, 0 });
                }
                else
                {
                    await s.WriteAsync(new byte[] { 5, 0 });
                }
                var req = new byte[4];
                await s.ReadExactlyAsync(req);
                IPAddress? ip = null;
                string? host = null;
                switch (req[3])
                {
                    case 1: { var b = new byte[4]; await s.ReadExactlyAsync(b); ip = new IPAddress(b); break; }
                    case 4: { var b = new byte[16]; await s.ReadExactlyAsync(b); ip = new IPAddress(b); break; }
                    case 3: { var l = new byte[1]; await s.ReadExactlyAsync(l); var b = new byte[l[0]]; await s.ReadExactlyAsync(b); host = System.Text.Encoding.ASCII.GetString(b); break; }
                }
                var portBytes = new byte[2];
                await s.ReadExactlyAsync(portBytes);
                var port = BinaryPrimitives.ReadUInt16BigEndian(portBytes);

                if (req[1] == 0xF0)
                {
                    Resolved.Add(host ?? "");
                    await s.WriteAsync(new byte[] { 5, 0, 0, 1, 10, 1, 2, 3, 0, 0 });
                    return;
                }
                var target = new IPEndPoint(ip!, port);
                Targets.Add(target);
                using var upstream = new TcpClient();
                try { await upstream.ConnectAsync(target); }
                catch (SocketException)
                {
                    await s.WriteAsync(new byte[] { 5, 5, 0, 1, 0, 0, 0, 0, 0, 0 });
                    return;
                }
                await s.WriteAsync(new byte[] { 5, 0, 0, 1, 0, 0, 0, 0, 0, 0 });
                var u = upstream.GetStream();
                await Task.WhenAny(s.CopyToAsync(u, _cts.Token), u.CopyToAsync(s, _cts.Token));
            }
            catch (Exception)
            {
                // Conexión de prueba cerrada.
            }
        }

        public void Dispose()
        {
            _cts.Cancel();
            _listener.Stop();
        }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var d in _disposables) d.Dispose();
        foreach (var node in _nodes) await node.DisposeAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var dir in _dirs)
        {
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}
