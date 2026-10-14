using System.Net;
using System.Net.Sockets;
using System.Numerics;
using System.Security.Cryptography;
using System.Threading.Channels;
using Shent.Core.Consensus;
using Shent.Core.Crypto;

namespace Shent.Core.P2P;

/// <summary>
/// Conexión con un par (TCP directo o circuito a través de un relay): lectura/escritura de tramas,
/// límites de tamaño y de frecuencia. Antes del handshake solo se aceptan tramas pequeñas.
/// </summary>
public sealed class PeerConnection
{
    private const int SendQueueCapacity = 4000;
    private const double MessagesPerSecond = 300;
    private const double BurstMessages = 1500;

    private readonly TcpClient _client;
    private readonly Stream _stream;
    private readonly NetworkParams _network;
    private readonly Channel<byte[]> _outgoing;
    private readonly CancellationTokenSource _cts = new();
    private readonly object _inventoryGate = new();
    private readonly HashSet<Hash256> _knownInventory = new();
    private readonly Message? _firstMessage;
    private double _tokens = BurstMessages;
    private long _lastRefill = Environment.TickCount64;
    private int _closed;

    public Guid Id { get; } = Guid.NewGuid();
    public bool Inbound { get; }
    /// <summary>Dirección marcada para conexiones salientes (null en conexiones entrantes).</summary>
    public NodeAddress? DialedAddress { get; init; }
    /// <summary>Si el par llegó por un circuito, el Peer ID del relay (RemoteEndPoint es entonces el relay).</summary>
    public PeerId? RelayedVia { get; init; }
    public bool IsRelayed => RelayedVia is not null;
    /// <summary>Peer ID que el par debe demostrar en el handshake (si se marcó una dirección con /p2p/ID).</summary>
    public PeerId? ExpectedPeerId { get; init; }
    /// <summary>Desafío aleatorio que el par debe firmar para probar su identidad.</summary>
    public Hash256 LocalChallenge { get; } = new(RandomNumberGenerator.GetBytes(Hash256.Size));
    public IPEndPoint RemoteEndPoint { get; }
    public IPAddress RemoteIp => PeerAddress.Normalize(RemoteEndPoint.Address);
    public DateTimeOffset ConnectedAt { get; } = DateTimeOffset.UtcNow;
    public DateTimeOffset LastReceived { get; private set; } = DateTimeOffset.UtcNow;

    public HelloMessage? RemoteHello { get; internal set; }
    /// <summary>Verdadero solo cuando la firma del desafío fue verificada.</summary>
    public bool AckReceived { get; internal set; }
    public bool HandshakeComplete => RemoteHello is not null && AckReceived;
    /// <summary>Identidad verificada del par (null hasta completar el handshake).</summary>
    public PeerId? RemotePeerId => HandshakeComplete ? RemoteHello!.NodeId : null;
    public long BestHeight { get; internal set; }
    public BigInteger BestWork { get; internal set; }
    public int Misbehavior { get; internal set; }
    public double? PingMs { get; internal set; }
    internal ulong PendingPingNonce { get; set; }
    internal long PingSentTicks { get; set; }
    internal DateTimeOffset LastDialBackServed { get; set; } = DateTimeOffset.MinValue;
    internal Hash256? PendingDialBack { get; set; }
    internal DateTimeOffset RelayRetryAfter { get; set; } = DateTimeOffset.MinValue;

    internal HashSet<Hash256> BlocksInFlight { get; } = new();
    /// <summary>El par entiende mensajes de Dandelion++ (fase de tallo).</summary>
    public bool SupportsDandelion => RemoteHello is { ProtocolVersion: >= Protocol.DandelionVersion };
    internal bool Syncing { get; set; }
    internal DateTimeOffset LastSyncActivity { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Dirección directa que el par anunció para recibir conexiones (solo si escucha y no llegó por relay).</summary>
    public PeerAddress? ListenAddress =>
        RemoteHello is { ListenPort: > 0 } hello && !IsRelayed ? new PeerAddress(RemoteIp, hello.ListenPort) : null;

    public string DisplayAddress
    {
        get
        {
            var endpoint = new PeerAddress(RemoteIp, RemoteEndPoint.Port).ToString();
            if (!IsRelayed) return endpoint;
            var id = RemoteHello?.NodeId ?? ExpectedPeerId;
            return $"{id?.Short ?? "par"} vía relay {endpoint}";
        }
    }

    public event Action<PeerConnection, string>? Closed;

    /// <param name="remoteEndPoint">Extremo real del par cuando el socket está conectado a un proxy SOCKS5.</param>
    public PeerConnection(TcpClient client, Stream stream, NetworkParams network, bool inbound, Message? firstMessage = null,
        IPEndPoint? remoteEndPoint = null)
    {
        _client = client;
        _client.NoDelay = true;
        _stream = stream;
        _network = network;
        _firstMessage = firstMessage;
        Inbound = inbound;
        RemoteEndPoint = remoteEndPoint ?? (IPEndPoint)client.Client.RemoteEndPoint!;
        _outgoing = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(SendQueueCapacity)
        {
            SingleReader = true,
            FullMode = BoundedChannelFullMode.Wait,
        });
    }

    public PeerConnection(TcpClient client, NetworkParams network, bool inbound)
        : this(client, client.GetStream(), network, inbound)
    {
    }

    public bool IsClosed => Volatile.Read(ref _closed) == 1;

    public bool Send(Message message)
    {
        if (IsClosed) return false;
        if (!_outgoing.Writer.TryWrite(Protocol.Frame(_network, message)))
        {
            Close("Cola de envío saturada.");
            return false;
        }
        return true;
    }

    public void MarkKnown(Hash256 hash)
    {
        lock (_inventoryGate)
        {
            if (_knownInventory.Count > 20_000) _knownInventory.Clear();
            _knownInventory.Add(hash);
        }
    }

    public bool Knows(Hash256 hash)
    {
        lock (_inventoryGate) return _knownInventory.Contains(hash);
    }

    public async Task RunAsync(Func<PeerConnection, Message, Task> onMessage, CancellationToken cancellation)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellation, _cts.Token);
        var token = linked.Token;
        var writer = Task.Run(() => WriteLoopAsync(token), token);
        try
        {
            if (_firstMessage is not null)
            {
                LastReceived = DateTimeOffset.UtcNow;
                await onMessage(this, _firstMessage).ConfigureAwait(false);
            }
            var maxPayload = Protocol.MaxPayload(_network);
            while (!token.IsCancellationRequested && !IsClosed)
            {
                var limit = HandshakeComplete ? maxPayload : Protocol.MaxPreHandshakePayload;
                var (type, payload) = await FrameIO.ReadAsync(_stream, _network, limit, token).ConfigureAwait(false);
                LastReceived = DateTimeOffset.UtcNow;
                if (!ConsumeToken())
                {
                    Misbehavior += 10;
                    if (Misbehavior >= 100)
                    {
                        Close("Demasiados mensajes por segundo.");
                        return;
                    }
                    continue;
                }

                Message message;
                try
                {
                    message = Protocol.Decode(type, payload);
                }
                catch (FormatException ex)
                {
                    Misbehavior = 100;
                    Close($"Mensaje malformado: {ex.Message}");
                    return;
                }
                await onMessage(this, message).ConfigureAwait(false);
            }
        }
        catch (FrameException ex)
        {
            Misbehavior = Math.Max(Misbehavior, 50);
            Close(ex.Message);
        }
        catch (OperationCanceledException)
        {
            Close("Conexión cancelada.");
        }
        catch (EndOfStreamException)
        {
            Close("El par cerró la conexión.");
        }
        catch (IOException ex)
        {
            Close($"Error de red: {ex.Message}");
        }
        catch (SocketException ex)
        {
            Close($"Error de socket: {ex.SocketErrorCode}");
        }
        catch (ObjectDisposedException)
        {
            Close("Conexión cerrada.");
        }
        finally
        {
            try { await writer.ConfigureAwait(false); } catch { /* el cierre ya fue registrado */ }
        }
    }

    private async Task WriteLoopAsync(CancellationToken token)
    {
        try
        {
            await foreach (var frame in _outgoing.Reader.ReadAllAsync(token).ConfigureAwait(false))
            {
                await _stream.WriteAsync(frame, token).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException or SocketException)
        {
            Close("Error al enviar datos.");
        }
    }

    private bool ConsumeToken()
    {
        var now = Environment.TickCount64;
        _tokens = Math.Min(BurstMessages, _tokens + (now - _lastRefill) / 1000.0 * MessagesPerSecond);
        _lastRefill = now;
        if (_tokens < 1) return false;
        _tokens -= 1;
        return true;
    }

    public void Close(string reason)
    {
        if (Interlocked.Exchange(ref _closed, 1) == 1) return;
        _outgoing.Writer.TryComplete();
        try { _cts.Cancel(); } catch (ObjectDisposedException) { }
        try { _client.Close(); } catch { /* ya cerrado */ }
        Closed?.Invoke(this, reason);
    }
}
