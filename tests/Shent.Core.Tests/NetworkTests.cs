using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using Shent.Core.Chain;
using Shent.Core.Consensus;
using Shent.Core.Crypto;
using Shent.Core.Mempool;
using Shent.Core.Mining;
using Shent.Core.Model;
using Shent.Core.Node;
using Shent.Core.P2P;

namespace Shent.Core.Tests;

public sealed class MinerTests : IDisposable
{
    private readonly TestChain _t = new();

    [Fact]
    public async Task CpuMinerFindsAndSubmitsBlocks()
    {
        _t.Now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var pool = new TxMempool(_t.Chain);
        using var key = TestChain.NewKey();
        var payout = TestChain.AddressOf(key);
        using var miner = new Miner(_t.Network.CreatePowHasher(),
            p => BlockTemplateBuilder.Build(_t.Chain, pool, p, "test"),
            b => _t.Chain.ProcessBlock(b));

        miner.Start(payout, 2, 100);
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (_t.Chain.Height < 3 && DateTime.UtcNow < deadline) await Task.Delay(50);
        miner.Stop();

        Assert.True(_t.Chain.Height >= 3);
        Assert.True(miner.BlocksFound >= 3);
        Assert.Equal(Emission.CumulativeSupply(_t.Network, _t.Chain.Height), _t.Chain.GetAccount(payout).Balance);
        Assert.True(_t.Chain.Reconcile().Consistent);
    }

    public void Dispose() => _t.Dispose();
}

public sealed class P2PTests : IAsyncDisposable
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

    private ShentNode StartNode(params string[] addNodes)
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
            AddNodes = addNodes.ToList(),
        };
        var node = ShentNode.Start(config);
        _nodes.Add(node);
        return node;
    }

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

    private static async Task WaitUntil(Func<bool> condition, int seconds = 20)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("La condición no se cumplió a tiempo.");
            await Task.Delay(50);
        }
    }

    [Fact]
    public async Task NewNodeDiscoversPeerSyncsAndReceivesNewBlocksAndTransactions()
    {
        using var alice = TestChain.NewKey();
        using var bob = TestChain.NewKey();
        var a = TestChain.AddressOf(alice);

        var nodeA = StartNode();
        for (var i = 0; i < 12; i++)
        {
            Assert.Equal(BlockAcceptStatus.NewTip, nodeA.Chain.ProcessBlock(MineBlock(nodeA, a)).Status);
        }

        var nodeB = StartNode($"127.0.0.1:{nodeA.P2P.ListeningPort}");
        await WaitUntil(() => nodeB.Chain.Height == 12);
        Assert.Equal(nodeA.Chain.Tip.Hash, nodeB.Chain.Tip.Hash);
        Assert.Equal(nodeA.Chain.GetAccount(a), nodeB.Chain.GetAccount(a));

        // Anuncio de bloque nuevo (NEW_BLOCK vía INV).
        nodeA.Chain.ProcessBlock(MineBlock(nodeA, a));
        await WaitUntil(() => nodeB.Chain.Height == 13);

        // Transacción enviada desde B que llega al mempool de A y A la mina.
        var tx = Transaction.CreateTransfer(alice, nodeB.Network.Magic, TestChain.AddressOf(bob), 7 * Money.Coin, 2000, 0);
        Assert.True(nodeB.SubmitTransaction(tx).Accepted);
        await WaitUntil(() => nodeA.Mempool.Contains(tx.Id));
        nodeA.Chain.ProcessBlock(MineBlock(nodeA, a));
        await WaitUntil(() => nodeB.Chain.GetAccount(TestChain.AddressOf(bob)).Balance == 7 * Money.Coin);
        Assert.Equal(0, nodeB.Mempool.Count);
        Assert.True(nodeB.Chain.Reconcile().Consistent);
    }

    [Fact]
    public async Task NodesConvergeOnChainWithMostWork()
    {
        using var minerA = TestChain.NewKey();
        using var minerB = TestChain.NewKey();
        var nodeA = StartNode();
        var nodeB = StartNode();
        for (var i = 0; i < 3; i++) nodeA.Chain.ProcessBlock(MineBlock(nodeA, TestChain.AddressOf(minerA)));
        for (var i = 0; i < 6; i++) nodeB.Chain.ProcessBlock(MineBlock(nodeB, TestChain.AddressOf(minerB)));

        Assert.True(await nodeA.P2P.AddPeerAsync($"127.0.0.1:{nodeB.P2P.ListeningPort}"));
        await WaitUntil(() => nodeA.Chain.Tip.Hash == nodeB.Chain.Tip.Hash);
        Assert.Equal(6, nodeA.Chain.Height);
        Assert.Equal(0, nodeA.Chain.GetAccount(TestChain.AddressOf(minerA)).Balance);
        Assert.True(nodeA.Chain.Reconcile().Consistent);
    }

    [Fact]
    public async Task MalformedFramesAndForeignNetworksAreDisconnected()
    {
        var node = StartNode();
        var port = node.P2P.ListeningPort;

        // Checksum incorrecto.
        using (var client = new TcpClient())
        {
            await client.ConnectAsync(IPAddress.Loopback, port);
            var stream = client.GetStream();
            var frame = new byte[Protocol.FrameHeaderSize + 4];
            BinaryPrimitives.WriteUInt32LittleEndian(frame, node.Network.Magic);
            frame[4] = (byte)MessageType.Ping;
            BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(5), 4);
            Assert.True(!await TryWrite(stream, frame) || await IsClosedByRemote(stream));
        }

        // Magic de otra red.
        using (var client = new TcpClient())
        {
            await client.ConnectAsync(IPAddress.Loopback, port);
            var stream = client.GetStream();
            Assert.True(!await TryWrite(stream, Protocol.Frame(NetworkParams.Mainnet, new PingMessage(1))) || await IsClosedByRemote(stream));
        }

        // Mensaje antes del handshake: penalización acumulada hasta la expulsión.
        using (var client = new TcpClient())
        {
            await client.ConnectAsync(IPAddress.Loopback, port);
            var stream = client.GetStream();
            var open = true;
            for (var i = 0; i < 5 && open; i++)
            {
                open = await TryWrite(stream, Protocol.Frame(node.Network, new GetPeersMessage()));
            }
            Assert.True(!open || await IsClosedByRemote(stream));
        }
    }

    /// <summary>El nodo puede cortar antes de que terminemos de escribir: eso también cuenta como desconexión.</summary>
    private static async Task<bool> TryWrite(NetworkStream stream, byte[] data)
    {
        try
        {
            await stream.WriteAsync(data);
            return true;
        }
        catch (IOException)
        {
            return false;
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
                var read = await stream.ReadAsync(buffer, timeout.Token);
                if (read == 0) return true;
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
    public void ProtocolMessagesRoundTrip()
    {
        using var identity = NodeIdentity.Ephemeral();
        using var relayIdentity = NodeIdentity.Ephemeral();
        var hello = new HelloMessage(Protocol.Version, 7, Hash256.Sha256("g"u8), 42, 123456, Hash256.Sha256("tip"u8), 37373,
            NodeServices.Relay, identity.PeerId, Hash256.Sha256("challenge"u8), IPAddress.Parse("198.51.100.9"), 50123, "SHENT/test", 1700000000);
        var payload = Protocol.EncodePayload(hello);
        Assert.Equal(hello, Protocol.Decode(MessageType.Hello, payload));

        var signature = identity.SignHandshake(7, Hash256.Sha256("c"u8), relayIdentity.PeerId);
        var ack = (HelloAckMessage)Protocol.Decode(MessageType.HelloAck, Protocol.EncodePayload(new HelloAckMessage(signature)));
        Assert.Equal(signature, ack.Signature);

        // Direcciones de documentación (RFC 5737): no son nodos reales.
        var endpoint = new PeerAddress(IPAddress.Parse("203.0.113.5"), 37373);
        var peers = new PeersMessage(
        [
            new NodeAddress(endpoint),
            new NodeAddress(endpoint, identity.PeerId),
            new NodeAddress(endpoint, relayIdentity.PeerId, identity.PeerId),
        ]);
        var decoded = (PeersMessage)Protocol.Decode(MessageType.Peers, Protocol.EncodePayload(peers));
        Assert.Equal(peers.Addresses, decoded.Addresses);

        var circuit = new CircuitConnectMessage(identity.PeerId);
        Assert.Equal(circuit, Protocol.Decode(MessageType.CircuitConnect, Protocol.EncodePayload(circuit)));
        var dialBack = new DialBackRequestMessage(Hash256.Sha256("n"u8), 37373);
        Assert.Equal(dialBack, Protocol.Decode(MessageType.DialBackRequest, Protocol.EncodePayload(dialBack)));
        Assert.Throws<FormatException>(() => Protocol.Decode(MessageType.CircuitStatus, [9]));

        Assert.Throws<FormatException>(() => Protocol.Decode(MessageType.Inv, [1, 0xFF, 0xFF, 0xFF, 0xFF, 0x0F]));
        Assert.Throws<FormatException>(() => Protocol.Decode((MessageType)200, []));
    }

    [Fact]
    public void AddressBookBacksOffFailuresAndBans()
    {
        var book = new AddressBook(null);
        var addr = new PeerAddress(IPAddress.Parse("198.51.100.7"), 37373);
        Assert.True(book.Add(addr, "seed"));
        Assert.False(book.Add(addr, "seed"));
        Assert.Single(book.PickCandidates(new HashSet<string>(), 10));

        book.MarkAttempt(addr);
        book.MarkFailure(addr);
        Assert.Empty(book.PickCandidates(new HashSet<string>(), 10));

        book.Ban(addr.Ip, TimeSpan.FromHours(1));
        Assert.True(book.IsBanned(addr.Ip));
        Assert.False(AddressBook.IsAcceptable(new PeerAddress(IPAddress.Parse("224.0.0.1"), 37373), false));
        Assert.False(AddressBook.IsAcceptable(new PeerAddress(IPAddress.Loopback, 37373), false));
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
