using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using Shent.Core.Node;
using Shent.Core.P2P;
using TextEncoding = System.Text.Encoding;

namespace Shent.Core.Tests;

/// <summary>
/// Descubrimiento automático por la DHT de BitTorrent, probado contra nodos DHT falsos en 127.0.0.1
/// (las pruebas no salen a Internet).
/// </summary>
public sealed class DhtDiscoveryTests : IAsyncDisposable
{
    private readonly List<ShentNode> _nodes = [];
    private readonly List<string> _dirs = [];
    private readonly List<IDisposable> _fakes = [];

    /// <summary>Nodo DHT mínimo: responde get_peers con los pares anunciados y acepta announce_peer con token válido.</summary>
    private sealed class FakeDhtNode : IDisposable
    {
        private static readonly byte[] ValidToken = TextEncoding.ASCII.GetBytes("token-ok");
        private readonly UdpClient _udp = new(new IPEndPoint(IPAddress.Loopback, 0));
        private readonly CancellationTokenSource _cts = new();
        private readonly object _gate = new();
        private readonly List<IPEndPoint> _stored = [];

        public byte[] Id { get; }
        public IPEndPoint Endpoint => (IPEndPoint)_udp.Client.LocalEndPoint!;
        public List<IPEndPoint> ExtraValues { get; } = [];
        public byte[]? NodesBlob { get; set; }
        public List<(int Port, IPEndPoint From)> Announces { get; } = [];
        public int RejectedAnnounces { get; private set; }

        public FakeDhtNode(byte[]? id = null)
        {
            Id = id ?? RandomNumberGenerator.GetBytes(20);
            _ = Task.Run(LoopAsync);
        }

        public List<IPEndPoint> Stored
        {
            get { lock (_gate) return [.. _stored]; }
        }

        private async Task LoopAsync()
        {
            while (!_cts.IsCancellationRequested)
            {
                UdpReceiveResult packet;
                try { packet = await _udp.ReceiveAsync(_cts.Token); }
                catch { return; }
                if (!Bencode.TryDecode(packet.Buffer, out var decoded) || decoded is not SortedDictionary<string, object> query) continue;
                if (query["y"] is not byte[] y || TextEncoding.ASCII.GetString(y) != "q") continue;
                var method = TextEncoding.ASCII.GetString((byte[])query["q"]);
                var args = (SortedDictionary<string, object>)query["a"];
                var reply = new SortedDictionary<string, object>(StringComparer.Ordinal) { ["id"] = Id };
                if (method == "get_peers")
                {
                    reply["token"] = ValidToken;
                    var values = Stored.Concat(ExtraValues).Select(p => (object)MainlineDht.EncodeCompactPeer(p)).ToList();
                    if (values.Count > 0) reply["values"] = values;
                    if (NodesBlob is not null) reply["nodes"] = NodesBlob;
                }
                else if (method == "announce_peer")
                {
                    if (args["token"] is not byte[] token || !token.SequenceEqual(ValidToken))
                    {
                        RejectedAnnounces++;
                        continue;
                    }
                    var port = (int)(long)args["port"];
                    lock (_gate)
                    {
                        Announces.Add((port, packet.RemoteEndPoint));
                        var peer = new IPEndPoint(packet.RemoteEndPoint.Address, port);
                        if (!_stored.Contains(peer)) _stored.Add(peer);
                    }
                }
                var message = new SortedDictionary<string, object>(StringComparer.Ordinal)
                {
                    ["t"] = query["t"],
                    ["y"] = "r",
                    ["r"] = reply,
                };
                await _udp.SendAsync(Bencode.Encode(message), packet.RemoteEndPoint);
            }
        }

        public void Dispose()
        {
            _cts.Cancel();
            _udp.Dispose();
        }
    }

    private FakeDhtNode Fake(byte[]? id = null)
    {
        var fake = new FakeDhtNode(id);
        _fakes.Add(fake);
        return fake;
    }

    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    private ShentNode StartNode(string router)
    {
        var dir = Path.Combine(Path.GetTempPath(), "shent-dht-tests", Guid.NewGuid().ToString("N"));
        _dirs.Add(dir);
        var node = ShentNode.Start(new NodeConfig
        {
            Network = "regtest",
            DataDirectory = dir,
            P2PPort = FreePort(),
            LanDiscovery = false,
            AllowLoopbackPeers = true,
            Upnp = false,
            Api = false,
            DhtRouters = [router],
        });
        _nodes.Add(node);
        return node;
    }

    private static bool ConnectedTo(ShentNode node, ShentNode other) =>
        node.P2P.GetPeers().Any(p => p.HandshakeComplete && p.PeerId == other.P2P.PeerId.ToString());

    private static async Task WaitUntil(Func<bool> condition, int seconds, string what)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException($"La condición no se cumplió a tiempo: {what}.");
            await Task.Delay(50);
        }
    }

    [Fact]
    public void BencodeMatchesTheSpecificationAndRejectsMalformedInput()
    {
        // Ejemplo literal de BEP 5 (consulta ping).
        var ping = new SortedDictionary<string, object>(StringComparer.Ordinal)
        {
            ["t"] = "aa",
            ["y"] = "q",
            ["q"] = "ping",
            ["a"] = new SortedDictionary<string, object>(StringComparer.Ordinal) { ["id"] = "abcdefghij0123456789" },
        };
        var encoded = Bencode.Encode(ping);
        Assert.Equal("d1:ad2:id20:abcdefghij0123456789e1:q4:ping1:t2:aa1:y1:qe", TextEncoding.ASCII.GetString(encoded));

        Assert.True(Bencode.TryDecode(encoded, out var decoded));
        var dict = Assert.IsType<SortedDictionary<string, object>>(decoded);
        Assert.Equal("ping", TextEncoding.ASCII.GetString((byte[])dict["q"]));
        Assert.True(Bencode.TryDecode("li-42el1:xee"u8, out var list));
        Assert.Equal(-42L, ((List<object>)list!)[0]);

        foreach (var bad in new[] { "d1:a", "i12", "5:abc", "d1:ai1eee", "x", "999999999:x", "i1e2", "d" + new string('l', 40) })
        {
            Assert.False(Bencode.TryDecode(TextEncoding.ASCII.GetBytes(bad), out _), bad);
        }
    }

    [Fact]
    public void CompactEncodingsAreParsedDefensively()
    {
        Assert.True(MainlineDht.TryParseCompactPeer([1, 2, 3, 4, 0x1F, 0x90], out var peer));
        Assert.Equal(new IPEndPoint(IPAddress.Parse("1.2.3.4"), 8080), peer);
        Assert.False(MainlineDht.TryParseCompactPeer([1, 2, 3, 4, 0, 0], out _));
        Assert.False(MainlineDht.TryParseCompactPeer([1, 2, 3], out _));

        var blob = new byte[26 * 3 + 5];
        blob[20] = 8; blob[21] = 8; blob[22] = 4; blob[23] = 4; blob[24] = 0x1A; blob[25] = 0xE1;
        blob[26 + 20] = 0; blob[26 + 24] = 1;
        blob[52 + 20] = 127; blob[52 + 23] = 1; blob[52 + 24] = 1;
        var nodes = MainlineDht.ParseCompactNodes(blob).ToList();
        Assert.Single(nodes);
        Assert.Equal(new IPEndPoint(IPAddress.Parse("8.8.4.4"), 6881), nodes[0].Endpoint);
    }

    [Fact]
    public void RendezvousKeyDependsOnNetworkAndGenesis()
    {
        var a = MainlineDht.InfoHashFor("mainnet", "aa");
        Assert.Equal(20, a.Length);
        Assert.Equal(a, MainlineDht.InfoHashFor("mainnet", "aa"));
        Assert.NotEqual(a, MainlineDht.InfoHashFor("testnet", "aa"));
        Assert.NotEqual(a, MainlineDht.InfoHashFor("mainnet", "bb"));
    }

    [Fact]
    public async Task LookupFollowsCloserNodesCollectsPeersAndAnnounces()
    {
        var infoHash = MainlineDht.InfoHashFor("regtest", "lookup-test");
        var closeId = (byte[])infoHash.Clone();
        closeId[19] ^= 1;
        var close = Fake(closeId);
        close.ExtraValues.Add(new IPEndPoint(IPAddress.Parse("203.0.113.10"), 37373));
        close.ExtraValues.Add(new IPEndPoint(IPAddress.Parse("198.51.100.7"), 40000));
        var router = Fake();
        router.NodesBlob = [.. close.Id, .. close.Endpoint.Address.GetAddressBytes(), (byte)(close.Endpoint.Port >> 8), (byte)close.Endpoint.Port];

        using var dht = new MainlineDht([router.Endpoint.ToString()], allowLoopback: true);
        Assert.True(dht.Start());
        var result = await dht.LookupAsync(infoHash, 37373, CancellationToken.None);

        Assert.Contains(new IPEndPoint(IPAddress.Parse("203.0.113.10"), 37373), result.Peers);
        Assert.Contains(new IPEndPoint(IPAddress.Parse("198.51.100.7"), 40000), result.Peers);
        Assert.Equal(2, result.Responses);
        Assert.Contains(close.Announces, a => a.Port == 37373);
        Assert.Equal(0, close.RejectedAnnounces);

        // Sin puerto (nodo no accesible) busca pero no se anuncia.
        var before = close.Announces.Count;
        await dht.LookupAsync(infoHash, null, CancellationToken.None);
        Assert.Equal(before, close.Announces.Count);
    }

    [Fact]
    public async Task UnresponsiveDhtEndsLookupWithoutPeers()
    {
        var silent = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        _fakes.Add(silent);
        using var dht = new MainlineDht([((IPEndPoint)silent.Client.LocalEndPoint!).ToString()], allowLoopback: true);
        Assert.True(dht.Start());
        var result = await dht.LookupAsync(MainlineDht.InfoHashFor("regtest", "x"), 1234, CancellationToken.None);
        Assert.Empty(result.Peers);
        Assert.Equal(0, result.Responses);
        Assert.Equal(0, result.Announced);
    }

    [Fact]
    public async Task NodesFindEachOtherThroughTheDhtWithoutAnyConfiguredAddress()
    {
        var tracker = Fake();
        // Basura publicada bajo la misma clave: un puerto sin nada y un servicio que no habla SHENT.
        var deadPort = FreePort();
        var impostor = new TcpListener(IPAddress.Loopback, 0);
        impostor.Start();
        _ = Task.Run(async () =>
        {
            while (true)
            {
                TcpClient client;
                try { client = await impostor.AcceptTcpClientAsync(); }
                catch { return; }
                try { await client.GetStream().WriteAsync(RandomNumberGenerator.GetBytes(64)); } catch { }
                client.Dispose();
            }
        });
        var impostorEndpoint = (IPEndPoint)impostor.LocalEndpoint;
        tracker.ExtraValues.Add(new IPEndPoint(IPAddress.Loopback, deadPort));
        tracker.ExtraValues.Add(impostorEndpoint);

        var nodeA = StartNode(tracker.Endpoint.ToString());
        await WaitUntil(() => tracker.Stored.Any(p => p.Port == nodeA.P2P.ListeningPort), 20, "A se anuncia en la DHT");
        Assert.Empty(nodeA.Config.Bootstrap);

        var nodeB = StartNode(tracker.Endpoint.ToString());
        await WaitUntil(() => ConnectedTo(nodeB, nodeA) && ConnectedTo(nodeA, nodeB), 30, "B encuentra a A por la DHT");
        await WaitUntil(() => nodeB.P2P.Dht.LastLookup is not null && nodeB.P2P.Dht.TotalAdded >= 1, 10, "estado DHT de B");
        Assert.True(nodeA.P2P.Dht.Announcing);

        // Las direcciones falsas no pasan el handshake, no se conectan y no se reenvían a otros pares.
        await WaitUntil(() => nodeB.P2P.AddressBook.GetFailures(new PeerAddress(IPAddress.Loopback, deadPort)) > 0, 20, "intento fallido a la dirección muerta");
        var peers = nodeB.P2P.GetPeers().Where(p => p.HandshakeComplete).ToList();
        Assert.All(peers, p => Assert.Equal(nodeA.P2P.PeerId.ToString(), p.PeerId));
        var shared = nodeB.P2P.AddressBook.GetShareable(100, includePrivate: true);
        Assert.DoesNotContain(shared, a => a.Endpoint.Port == deadPort || a.Endpoint.Port == impostorEndpoint.Port);
        impostor.Stop();
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var node in _nodes) await node.DisposeAsync();
        foreach (var fake in _fakes) fake.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var dir in _dirs)
        {
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}
