using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using Shent.Core.Consensus;
using Shent.Core.Crypto;
using Shent.Core.Node;

namespace Shent.Core.P2P;

/// <summary>
/// Relay de circuitos para nodos que no pueden recibir conexiones (NAT estricto, CGNAT, firewall).
///
/// 1. El nodo N (sin puertos abiertos) mantiene una conexión normal con el relay R y le envía RELAY_RESERVE.
/// 2. Un nodo D abre una conexión TCP nueva a R y envía CIRCUIT_CONNECT(N).
/// 3. R avisa a N con CIRCUIT_INCOMING(id) por la conexión existente; N abre otra conexión TCP a R con CIRCUIT_ACCEPT(id).
/// 4. R responde CIRCUIT_STATUS(ok) a ambos y copia bytes en las dos direcciones.
/// 5. D y N hacen el handshake normal de SHENT a través del circuito: se autentican de extremo a extremo
///    con sus Peer ID, así que el relay no puede hacerse pasar por ninguno ni alterar mensajes sin que se note
///    (los bloques y transacciones además se validan completos como siempre).
///
/// El relay no valida bloques en nombre de nadie ni tiene autoridad sobre la red: solo reenvía bytes, con
/// límites de reservas, circuitos, duración y volumen para que no lo puedan usar como proxy gratuito.
/// </summary>
public sealed class RelayService
{
    public const int MaxReservations = 128;
    public const int MaxCircuits = 64;
    public const int MaxCircuitsPerTarget = 8;
    public const int MaxCircuitsPerSourceIp = 8;
    public static readonly TimeSpan ReservationTtl = TimeSpan.FromHours(1);
    public static readonly TimeSpan CircuitMaxDuration = TimeSpan.FromHours(2);
    public const long CircuitMaxBytes = 512L * 1024 * 1024;
    private static readonly TimeSpan AcceptTimeout = TimeSpan.FromSeconds(10);

    private sealed record Reservation(PeerConnection Control, DateTimeOffset Expires);

    private sealed class PendingCircuit(PeerId target, IPAddress targetIp)
    {
        public PeerId Target { get; } = target;
        public IPAddress TargetIp { get; } = targetIp;
        public TaskCompletionSource<(TcpClient Client, Stream Stream)> Accepted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private readonly NetworkParams _network;
    private readonly NodeLog _log;
    private readonly ConcurrentDictionary<PeerId, Reservation> _reservations = new();
    private readonly ConcurrentDictionary<Hash256, PendingCircuit> _pending = new();
    private readonly ConcurrentDictionary<PeerId, int> _circuitsPerTarget = new();
    private readonly ConcurrentDictionary<string, int> _circuitsPerIp = new();
    private int _activeCircuits;
    private long _bytesRelayed;
    private long _circuitsTotal;

    public RelayService(NetworkParams network, NodeLog log)
    {
        _network = network;
        _log = log;
    }

    public int ActiveCircuits => Volatile.Read(ref _activeCircuits);
    public int ReservationCount => _reservations.Count(r => IsLive(r.Value));
    public long BytesRelayed => Interlocked.Read(ref _bytesRelayed);
    public long CircuitsTotal => Interlocked.Read(ref _circuitsTotal);

    private static bool IsLive(Reservation r) => !r.Control.IsClosed && r.Expires > DateTimeOffset.UtcNow;

    /// <summary>Peer ID de los nodos con reserva vigente (para anunciar sus direcciones de circuito).</summary>
    public List<PeerId> ReservedPeers() => _reservations.Where(r => IsLive(r.Value)).Select(r => r.Key).ToList();

    public RelayStatus Reserve(PeerConnection peer, out long expiresAt)
    {
        expiresAt = 0;
        if (peer.RemotePeerId is not { } id || peer.IsRelayed) return RelayStatus.Refused;
        foreach (var (key, value) in _reservations)
        {
            if (!IsLive(value)) _reservations.TryRemove(key, out _);
        }
        if (!_reservations.ContainsKey(id) && _reservations.Count >= MaxReservations) return RelayStatus.Unavailable;
        var expires = DateTimeOffset.UtcNow + ReservationTtl;
        var isNew = !_reservations.ContainsKey(id);
        _reservations[id] = new Reservation(peer, expires);
        expiresAt = expires.ToUnixTimeSeconds();
        if (isNew) _log.Info("relay", $"Reserva de relay concedida a {id.Short} ({peer.DisplayAddress}).");
        return RelayStatus.Ok;
    }

    public void OnPeerClosed(PeerConnection peer)
    {
        foreach (var (key, value) in _reservations)
        {
            if (value.Control == peer) _reservations.TryRemove(key, out _);
        }
    }

    /// <summary>Un nodo quiere llegar a <paramref name="request"/>.Target a través de este relay.</summary>
    public async Task HandleConnectAsync(TcpClient dialer, Stream dialerStream, CircuitConnectMessage request, CancellationToken cancellation)
    {
        var sourceIp = PeerAddress.Normalize(((IPEndPoint)dialer.Client.RemoteEndPoint!).Address).ToString();
        var target = request.Target;
        if (!_reservations.TryGetValue(target, out var reservation) || !IsLive(reservation))
        {
            await RejectAsync(dialer, dialerStream, RelayStatus.NoReservation, cancellation).ConfigureAwait(false);
            return;
        }
        if (ActiveCircuits >= MaxCircuits
            || _circuitsPerTarget.GetValueOrDefault(target) >= MaxCircuitsPerTarget
            || _circuitsPerIp.GetValueOrDefault(sourceIp) >= MaxCircuitsPerSourceIp)
        {
            await RejectAsync(dialer, dialerStream, RelayStatus.Unavailable, cancellation).ConfigureAwait(false);
            return;
        }

        var circuitId = new Hash256(RandomNumberGenerator.GetBytes(Hash256.Size));
        var pending = new PendingCircuit(target, reservation.Control.RemoteIp);
        _pending[circuitId] = pending;
        try
        {
            reservation.Control.Send(new CircuitIncomingMessage(circuitId));
            var accepted = await pending.Accepted.Task.WaitAsync(AcceptTimeout, cancellation).ConfigureAwait(false);
            await SpliceAsync(dialer, dialerStream, accepted.Client, accepted.Stream, target, sourceIp, cancellation).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            await RejectAsync(dialer, dialerStream, RelayStatus.Unavailable, cancellation).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            dialer.Dispose();
        }
        finally
        {
            _pending.TryRemove(circuitId, out _);
        }
    }

    /// <summary>El nodo con reserva acepta el circuito abriendo una conexión nueva.</summary>
    public async Task HandleAcceptAsync(TcpClient client, Stream stream, CircuitAcceptMessage accept, CancellationToken cancellation)
    {
        var ip = PeerAddress.Normalize(((IPEndPoint)client.Client.RemoteEndPoint!).Address);
        if (!_pending.TryRemove(accept.CircuitId, out var pending) || !pending.TargetIp.Equals(ip))
        {
            await RejectAsync(client, stream, RelayStatus.Refused, cancellation).ConfigureAwait(false);
            return;
        }
        if (!pending.Accepted.TrySetResult((client, stream))) client.Dispose();
    }

    private async Task RejectAsync(TcpClient client, Stream stream, RelayStatus status, CancellationToken cancellation)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            cts.CancelAfter(TimeSpan.FromSeconds(3));
            await FrameIO.WriteAsync(stream, _network, new CircuitStatusMessage(status), cts.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or ObjectDisposedException)
        {
        }
        finally
        {
            client.Dispose();
        }
    }

    private async Task SpliceAsync(TcpClient a, Stream aStream, TcpClient b, Stream bStream, PeerId target, string sourceIp, CancellationToken cancellation)
    {
        Interlocked.Increment(ref _activeCircuits);
        Interlocked.Increment(ref _circuitsTotal);
        _circuitsPerTarget.AddOrUpdate(target, 1, (_, n) => n + 1);
        _circuitsPerIp.AddOrUpdate(sourceIp, 1, (_, n) => n + 1);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        cts.CancelAfter(CircuitMaxDuration);
        var budget = new long[] { CircuitMaxBytes };
        try
        {
            a.NoDelay = true;
            b.NoDelay = true;
            await FrameIO.WriteAsync(aStream, _network, new CircuitStatusMessage(RelayStatus.Ok), cts.Token).ConfigureAwait(false);
            await FrameIO.WriteAsync(bStream, _network, new CircuitStatusMessage(RelayStatus.Ok), cts.Token).ConfigureAwait(false);
            _log.Info("relay", $"Circuito abierto hacia {target.Short} (activos: {ActiveCircuits}).");
            var first = await Task.WhenAny(
                PumpAsync(aStream, bStream, budget, cts.Token),
                PumpAsync(bStream, aStream, budget, cts.Token)).ConfigureAwait(false);
            await first.ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or ObjectDisposedException)
        {
        }
        finally
        {
            try { cts.Cancel(); } catch (ObjectDisposedException) { }
            a.Dispose();
            b.Dispose();
            Interlocked.Decrement(ref _activeCircuits);
            _circuitsPerTarget.AddOrUpdate(target, 0, (_, n) => Math.Max(0, n - 1));
            _circuitsPerIp.AddOrUpdate(sourceIp, 0, (_, n) => Math.Max(0, n - 1));
        }
    }

    private async Task PumpAsync(Stream from, Stream to, long[] budget, CancellationToken cancellation)
    {
        var buffer = new byte[16 * 1024];
        try
        {
            while (true)
            {
                var n = await from.ReadAsync(buffer, cancellation).ConfigureAwait(false);
                if (n == 0) return;
                if (Interlocked.Add(ref budget[0], -n) < 0) return;
                Interlocked.Add(ref _bytesRelayed, n);
                await to.WriteAsync(buffer.AsMemory(0, n), cancellation).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or ObjectDisposedException)
        {
        }
    }
}
