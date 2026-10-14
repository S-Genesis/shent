using System.Buffers.Binary;
using System.Security.Cryptography;
using Shent.Core.Consensus;

namespace Shent.Core.P2P;

/// <summary>Trama inválida: magic de otra red, tamaño excesivo o checksum incorrecto.</summary>
public sealed class FrameException(string message) : Exception(message);

/// <summary>Lectura y escritura de tramas sobre cualquier flujo (TCP directo o circuito de relay).</summary>
public static class FrameIO
{
    public static async Task<(MessageType Type, byte[] Payload)> ReadAsync(Stream stream, NetworkParams network, int maxPayload, CancellationToken cancellation)
    {
        var header = new byte[Protocol.FrameHeaderSize];
        await stream.ReadExactlyAsync(header, cancellation).ConfigureAwait(false);
        var magic = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(0, 4));
        if (magic != network.Magic) throw new FrameException("Magic de red incorrecto.");
        var type = (MessageType)header[4];
        var length = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(5, 4));
        if (length > maxPayload) throw new FrameException($"Mensaje demasiado grande ({length} bytes, máximo {maxPayload}).");
        var payload = new byte[length];
        await stream.ReadExactlyAsync(payload, cancellation).ConfigureAwait(false);
        var checksum = SHA256.HashData(payload);
        if (!checksum.AsSpan(0, 4).SequenceEqual(header.AsSpan(9, 4))) throw new FrameException("Checksum inválido.");
        return (type, payload);
    }

    /// <summary>Lee y decodifica un mensaje corto con plazo (usado antes del handshake y en el relay).</summary>
    public static async Task<Message> ReadMessageAsync(Stream stream, NetworkParams network, TimeSpan timeout, CancellationToken cancellation)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        cts.CancelAfter(timeout);
        var (type, payload) = await ReadAsync(stream, network, Protocol.MaxPreHandshakePayload, cts.Token).ConfigureAwait(false);
        return Protocol.Decode(type, payload);
    }

    public static async Task WriteAsync(Stream stream, NetworkParams network, Message message, CancellationToken cancellation)
    {
        await stream.WriteAsync(Protocol.Frame(network, message), cancellation).ConfigureAwait(false);
        await stream.FlushAsync(cancellation).ConfigureAwait(false);
    }
}
