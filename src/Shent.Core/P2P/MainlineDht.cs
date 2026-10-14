using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using TextEncoding = System.Text.Encoding;

namespace Shent.Core.P2P;

public sealed record DhtLookupResult(IReadOnlyList<IPEndPoint> Peers, int NodesQueried, int Responses, int Announced);

/// <summary>
/// Cliente de la DHT pública de BitTorrent (Mainline, BEP 5) usado solo como punto de encuentro:
/// cada nodo SHENT publica su puerto TCP bajo una clave derivada del génesis de su red y busca a los demás.
///
/// La DHT solo entrega pares IP:puerto sin autenticar. No decide nada: cualquier dirección obtenida
/// tiene que pasar el handshake firmado (red, génesis y Peer ID) y cada bloque se valida localmente.
/// El cliente se anuncia como "solo lectura" (BEP 43): no guarda datos de otros ni responde consultas.
/// </summary>
public sealed class MainlineDht : IDisposable
{
    public static readonly IReadOnlyList<string> DefaultRouters =
    [
        "router.bittorrent.com:6881",
        "dht.transmissionbt.com:6881",
        "router.utorrent.com:6881",
        "dht.libtorrent.org:25401",
    ];

    private const int K = 8;
    private const int Alpha = 8;
    private const int MaxQueriesPerLookup = 160;
    private const int MaxCandidates = 2000;
    private const int MaxPeersPerLookup = 200;
    private const int MaxPacketBytes = 4096;
    private const int MaxCachedNodes = 64;
    private static readonly TimeSpan QueryTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan LookupBudget = TimeSpan.FromSeconds(25);

    private readonly IReadOnlyList<string> _routers;
    private readonly bool _allowLoopback;
    private readonly byte[] _nodeId = RandomNumberGenerator.GetBytes(20);
    private readonly ConcurrentDictionary<ushort, Pending> _pending = new();
    private readonly object _cacheGate = new();
    private readonly List<IPEndPoint> _goodNodes = new();
    private readonly CancellationTokenSource _cts = new();
    private UdpClient? _udp;
    private int _nextTransaction = RandomNumberGenerator.GetInt32(ushort.MaxValue);

    private sealed record Pending(IPEndPoint Target, TaskCompletionSource<SortedDictionary<string, object>> Completion);

    private sealed class Candidate(IPEndPoint endpoint, byte[]? id)
    {
        public IPEndPoint Endpoint { get; } = endpoint;
        public byte[]? Id { get; set; } = id;
        public bool Queried { get; set; }
        public byte[]? Token { get; set; }
        public bool Responded { get; set; }
    }

    public MainlineDht(IEnumerable<string>? routers = null, bool allowLoopback = false)
    {
        _routers = routers?.ToList() ?? DefaultRouters;
        _allowLoopback = allowLoopback;
    }

    public bool IsRunning => _udp is not null;

    /// <summary>Clave de encuentro: SHA-1 de la red y su génesis. Nodos de otra red o versión de consenso no coinciden.</summary>
    public static byte[] InfoHashFor(string networkName, string genesisHash) =>
        SHA1.HashData(TextEncoding.UTF8.GetBytes($"SHENT-DHT-V1:{networkName}:{genesisHash}"));

    public bool Start(Action<string>? warn = null)
    {
        try
        {
            var udp = new UdpClient(new IPEndPoint(IPAddress.Any, 0));
            if (OperatingSystem.IsWindows())
            {
                // Evita que un ICMP "puerto inalcanzable" de un nodo caído cierre el socket UDP en Windows.
                const int SioUdpConnreset = -1744830452;
                udp.Client.IOControl(SioUdpConnreset, [0, 0, 0, 0], null);
            }
            _udp = udp;
        }
        catch (SocketException ex)
        {
            warn?.Invoke($"No se pudo abrir el socket UDP de la DHT ({ex.SocketErrorCode}).");
            return false;
        }
        _ = Task.Run(() => ReceiveLoopAsync(_cts.Token));
        return true;
    }

    /// <summary>
    /// Búsqueda iterativa get_peers hacia la clave; si <paramref name="announcePort"/> tiene valor,
    /// al terminar se anuncia ese puerto TCP en los nodos más cercanos que entregaron token.
    /// </summary>
    public async Task<DhtLookupResult> LookupAsync(byte[] infoHash, int? announcePort, CancellationToken token)
    {
        if (_udp is null) throw new InvalidOperationException("La DHT no está iniciada.");
        if (infoHash.Length != 20) throw new ArgumentException("La clave debe tener 20 bytes.", nameof(infoHash));

        using var budget = CancellationTokenSource.CreateLinkedTokenSource(token, _cts.Token);
        budget.CancelAfter(LookupBudget);
        var candidates = new Dictionary<string, Candidate>();
        var peers = new Dictionary<string, IPEndPoint>();
        var queried = 0;
        var responses = 0;

        foreach (var node in await SeedNodesAsync(budget.Token).ConfigureAwait(false))
        {
            candidates.TryAdd(node.ToString(), new Candidate(node, null));
        }

        try
        {
            while (queried < MaxQueriesPerLookup && !budget.IsCancellationRequested)
            {
                var batch = NextBatch(candidates, infoHash);
                if (batch.Count == 0) break;
                foreach (var c in batch) c.Queried = true;
                queried += batch.Count;

                var replies = await Task.WhenAll(batch.Select(c => QueryAsync(c.Endpoint, GetPeersQuery(infoHash), budget.Token)))
                    .ConfigureAwait(false);
                for (var i = 0; i < batch.Count; i++)
                {
                    if (replies[i] is not { } reply) continue;
                    responses++;
                    var candidate = batch[i];
                    candidate.Responded = true;
                    candidate.Id ??= (byte[])reply["id"];
                    if (reply.TryGetValue("token", out var tok) && tok is byte[] tokenBytes && tokenBytes.Length <= 64) candidate.Token = tokenBytes;
                    RememberGoodNode(candidate.Endpoint);

                    if (reply.TryGetValue("values", out var values) && values is List<object> list)
                    {
                        foreach (var item in list)
                        {
                            if (peers.Count >= MaxPeersPerLookup) break;
                            if (item is byte[] compact && TryParseCompactPeer(compact, out var peer)) peers.TryAdd(peer.ToString(), peer);
                        }
                    }
                    if (reply.TryGetValue("nodes", out var nodes) && nodes is byte[] nodeBytes)
                    {
                        foreach (var (id, endpoint) in ParseCompactNodes(nodeBytes, _allowLoopback))
                        {
                            if (candidates.Count >= MaxCandidates) break;
                            candidates.TryAdd(endpoint.ToString(), new Candidate(endpoint, id));
                        }
                    }
                }
            }
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            // Se agotó el tiempo de la búsqueda: se usa lo encontrado hasta ahora.
        }

        var announced = 0;
        if (announcePort is int port and > 0 and <= 65535 && !token.IsCancellationRequested)
        {
            var closest = candidates.Values
                .Where(c => c.Responded && c.Token is not null && c.Id is not null)
                .OrderBy(c => c.Id!, new DistanceComparer(infoHash))
                .Take(K)
                .ToList();
            using var announceBudget = CancellationTokenSource.CreateLinkedTokenSource(token, _cts.Token);
            announceBudget.CancelAfter(TimeSpan.FromSeconds(5));
            var acks = await Task.WhenAll(closest.Select(c => QueryAsync(c.Endpoint, AnnounceQuery(infoHash, port, c.Token!), announceBudget.Token)))
                .ConfigureAwait(false);
            announced = acks.Count(a => a is not null);
        }

        return new DhtLookupResult(peers.Values.ToList(), queried, responses, announced);
    }

    private static List<Candidate> NextBatch(Dictionary<string, Candidate> candidates, byte[] target)
    {
        var comparer = new DistanceComparer(target);
        // Los routers de arranque no tienen ID conocido: se consultan primero.
        var unknown = candidates.Values.Where(c => !c.Queried && c.Id is null).Take(Alpha).ToList();
        if (unknown.Count > 0) return unknown;

        var responded = candidates.Values.Where(c => c.Responded && c.Id is not null).OrderBy(c => c.Id!, comparer).Take(K).ToList();
        var pending = candidates.Values.Where(c => !c.Queried && c.Id is not null).OrderBy(c => c.Id!, comparer).Take(Alpha).ToList();
        if (pending.Count == 0) return pending;
        // Termina cuando ningún nodo sin consultar está más cerca que los K más cercanos que respondieron.
        if (responded.Count >= K && comparer.Compare(pending[0].Id!, responded[^1].Id!) >= 0) return [];
        return pending;
    }

    private async Task<List<IPEndPoint>> SeedNodesAsync(CancellationToken token)
    {
        var seeds = new List<IPEndPoint>();
        lock (_cacheGate) seeds.AddRange(_goodNodes);
        foreach (var router in _routers)
        {
            var colon = router.LastIndexOf(':');
            if (colon <= 0 || !int.TryParse(router[(colon + 1)..], out var port)) continue;
            var host = router[..colon];
            try
            {
                if (IPAddress.TryParse(host, out var literal))
                {
                    seeds.Add(new IPEndPoint(literal, port));
                    continue;
                }
                using var dnsBudget = CancellationTokenSource.CreateLinkedTokenSource(token);
                dnsBudget.CancelAfter(TimeSpan.FromSeconds(5));
                var addresses = await Dns.GetHostAddressesAsync(host, AddressFamily.InterNetwork, dnsBudget.Token).ConfigureAwait(false);
                seeds.AddRange(addresses.Take(2).Select(a => new IPEndPoint(a, port)));
            }
            catch (Exception ex) when (ex is SocketException or OperationCanceledException or ArgumentException)
            {
                // Router de arranque no disponible: se usan los demás y los nodos recordados.
            }
        }
        return seeds;
    }

    private void RememberGoodNode(IPEndPoint endpoint)
    {
        lock (_cacheGate)
        {
            if (_goodNodes.Contains(endpoint)) return;
            if (_goodNodes.Count >= MaxCachedNodes) _goodNodes.RemoveAt(0);
            _goodNodes.Add(endpoint);
        }
    }

    private SortedDictionary<string, object> GetPeersQuery(byte[] infoHash) => new(StringComparer.Ordinal)
    {
        ["y"] = "q",
        ["q"] = "get_peers",
        ["ro"] = 1,
        ["a"] = new SortedDictionary<string, object>(StringComparer.Ordinal) { ["id"] = _nodeId, ["info_hash"] = infoHash },
    };

    private SortedDictionary<string, object> AnnounceQuery(byte[] infoHash, int port, byte[] token) => new(StringComparer.Ordinal)
    {
        ["y"] = "q",
        ["q"] = "announce_peer",
        ["ro"] = 1,
        ["a"] = new SortedDictionary<string, object>(StringComparer.Ordinal)
        {
            ["id"] = _nodeId,
            ["info_hash"] = infoHash,
            ["port"] = port,
            ["implied_port"] = 0,
            ["token"] = token,
        },
    };

    private async Task<SortedDictionary<string, object>?> QueryAsync(IPEndPoint target, SortedDictionary<string, object> message, CancellationToken token)
    {
        var udp = _udp;
        if (udp is null || token.IsCancellationRequested) return null;
        var tid = (ushort)Interlocked.Increment(ref _nextTransaction);
        var pending = new Pending(target, new TaskCompletionSource<SortedDictionary<string, object>>(TaskCreationOptions.RunContinuationsAsynchronously));
        if (!_pending.TryAdd(tid, pending)) return null;
        message["t"] = new[] { (byte)(tid >> 8), (byte)tid };
        try
        {
            await udp.SendAsync(Bencode.Encode(message), target, token).ConfigureAwait(false);
            return await pending.Completion.Task.WaitAsync(QueryTimeout, token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is TimeoutException or SocketException or OperationCanceledException or ObjectDisposedException)
        {
            return null;
        }
        finally
        {
            _pending.TryRemove(tid, out _);
        }
    }

    private async Task ReceiveLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested && _udp is { } udp)
        {
            UdpReceiveResult packet;
            try
            {
                packet = await udp.ReceiveAsync(token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            catch (SocketException)
            {
                continue;
            }
            HandlePacket(packet.Buffer, packet.RemoteEndPoint);
        }
    }

    private void HandlePacket(byte[] buffer, IPEndPoint from)
    {
        if (buffer.Length > MaxPacketBytes) return;
        if (!Bencode.TryDecode(buffer, out var decoded) || decoded is not SortedDictionary<string, object> message) return;
        if (!message.TryGetValue("t", out var t) || t is not byte[] { Length: 2 } tid) return;
        if (!message.TryGetValue("y", out var y) || y is not byte[] kind) return;
        var key = (ushort)(tid[0] << 8 | tid[1]);
        if (!_pending.TryGetValue(key, out var pending)) return;
        // La respuesta tiene que venir del nodo consultado: evita que un tercero inyecte resultados.
        if (!Normalize(pending.Target).Equals(Normalize(from))) return;

        if (kind is [(byte)'r'] && message.TryGetValue("r", out var r) && r is SortedDictionary<string, object> reply
            && reply.TryGetValue("id", out var id) && id is byte[] { Length: 20 })
        {
            pending.Completion.TrySetResult(reply);
        }
        else if (kind is [(byte)'e'])
        {
            pending.Completion.TrySetCanceled();
        }
    }

    private static IPEndPoint Normalize(IPEndPoint endpoint) =>
        endpoint.Address.IsIPv4MappedToIPv6 ? new IPEndPoint(endpoint.Address.MapToIPv4(), endpoint.Port) : endpoint;

    public static bool TryParseCompactPeer(byte[] compact, out IPEndPoint peer)
    {
        peer = null!;
        if (compact.Length != 6) return false;
        var port = compact[4] << 8 | compact[5];
        if (port == 0) return false;
        peer = new IPEndPoint(new IPAddress(compact.AsSpan(0, 4)), port);
        return true;
    }

    public static IEnumerable<(byte[] Id, IPEndPoint Endpoint)> ParseCompactNodes(byte[] data, bool allowLoopback = false)
    {
        for (var i = 0; i + 26 <= data.Length; i += 26)
        {
            var ip = new IPAddress(data.AsSpan(i + 20, 4));
            var port = data[i + 24] << 8 | data[i + 25];
            var first = data[i + 20];
            if (port == 0 || first is 0 or >= 224 || (IPAddress.IsLoopback(ip) && !allowLoopback)) continue;
            yield return (data.AsSpan(i, 20).ToArray(), new IPEndPoint(ip, port));
        }
    }

    public static byte[] EncodeCompactPeer(IPEndPoint peer)
    {
        var bytes = new byte[6];
        peer.Address.MapToIPv4().GetAddressBytes().CopyTo(bytes, 0);
        bytes[4] = (byte)(peer.Port >> 8);
        bytes[5] = (byte)peer.Port;
        return bytes;
    }

    private sealed class DistanceComparer(byte[] target) : IComparer<byte[]>
    {
        public int Compare(byte[]? a, byte[]? b)
        {
            for (var i = 0; i < 20; i++)
            {
                var da = a![i] ^ target[i];
                var db = b![i] ^ target[i];
                if (da != db) return da.CompareTo(db);
            }
            return 0;
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _udp?.Dispose();
        _udp = null;
        foreach (var pending in _pending.Values) pending.Completion.TrySetCanceled();
        _cts.Dispose();
    }
}
