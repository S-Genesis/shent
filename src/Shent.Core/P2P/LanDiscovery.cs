using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;

namespace Shent.Core.P2P;

/// <summary>
/// Descubrimiento de nodos en la misma red local mediante difusión UDP. Permite que varias PC de una
/// casa u oficina se encuentren sin nodos semilla. Solo aporta candidatos: la conexión real sigue
/// pasando por el handshake y la validación habituales.
/// </summary>
public sealed class LanDiscovery : IDisposable
{
    private static readonly byte[] Prefix = "SHENTLAN"u8.ToArray();
    private const int PacketSize = 8 + 4 + 8 + 2;

    private readonly uint _magic;
    private readonly ulong _nodeNonce;
    private readonly int _udpPort;
    private readonly Func<int> _listenPort;
    private readonly Action<PeerAddress> _onDiscovered;
    private UdpClient? _udp;
    private CancellationTokenSource? _cts;

    public bool Active { get; private set; }

    public LanDiscovery(uint magic, ulong nodeNonce, int udpPort, Func<int> listenPort, Action<PeerAddress> onDiscovered)
    {
        _magic = magic;
        _nodeNonce = nodeNonce;
        _udpPort = udpPort;
        _listenPort = listenPort;
        _onDiscovered = onDiscovered;
    }

    public bool Start(Action<string> log)
    {
        try
        {
            var udp = new UdpClient(AddressFamily.InterNetwork);
            udp.ExclusiveAddressUse = false;
            udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            udp.EnableBroadcast = true;
            udp.Client.Bind(new IPEndPoint(IPAddress.Any, _udpPort));
            _udp = udp;
        }
        catch (SocketException ex)
        {
            log($"Descubrimiento LAN desactivado: no se pudo abrir UDP {_udpPort} ({ex.SocketErrorCode}).");
            return false;
        }
        _cts = new CancellationTokenSource();
        Active = true;
        _ = Task.Run(() => ReceiveLoopAsync(_cts.Token));
        _ = Task.Run(() => AnnounceLoopAsync(_cts.Token));
        log($"Descubrimiento LAN activo en UDP {_udpPort}.");
        return true;
    }

    private byte[] BuildPacket()
    {
        var packet = new byte[PacketSize];
        Prefix.CopyTo(packet, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(8), _magic);
        BinaryPrimitives.WriteUInt64LittleEndian(packet.AsSpan(12), _nodeNonce);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(20), (ushort)_listenPort());
        return packet;
    }

    private async Task AnnounceLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            if (_listenPort() > 0 && _udp is not null)
            {
                try
                {
                    await _udp.SendAsync(BuildPacket(), new IPEndPoint(IPAddress.Broadcast, _udpPort), token).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
                {
                    // La red puede no permitir difusión; se reintenta en el próximo ciclo.
                }
            }
            try { await Task.Delay(TimeSpan.FromSeconds(30), token).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
        }
    }

    private async Task ReceiveLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested && _udp is not null)
        {
            try
            {
                var result = await _udp.ReceiveAsync(token).ConfigureAwait(false);
                var data = result.Buffer;
                if (data.Length != PacketSize || !data.AsSpan(0, 8).SequenceEqual(Prefix)) continue;
                if (BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(8)) != _magic) continue;
                if (BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(12)) == _nodeNonce) continue;
                var port = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(20));
                if (port == 0) continue;
                _onDiscovered(new PeerAddress(PeerAddress.Normalize(result.RemoteEndPoint.Address), port));
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
            {
                if (token.IsCancellationRequested) return;
                await Task.Delay(1000, CancellationToken.None).ConfigureAwait(false);
            }
        }
    }

    public void Dispose()
    {
        Active = false;
        _cts?.Cancel();
        _udp?.Dispose();
        _udp = null;
    }
}
