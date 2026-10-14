using System.Net;
using System.Net.Sockets;
using System.Numerics;
using System.Security.Cryptography;
using Shent.Core.Consensus;
using Shent.Core.Crypto;
using Shent.Core.Encoding;

namespace Shent.Core.P2P;

public enum MessageType : byte
{
    Hello = 1,
    HelloAck = 2,
    Ping = 3,
    Pong = 4,
    GetPeers = 5,
    Peers = 6,
    GetBlocks = 7,
    Inv = 8,
    GetData = 9,
    Block = 10,
    Tx = 11,
    NotFound = 12,

    // Relay de circuitos (para nodos detrás de NAT/CGNAT sin puertos abiertos).
    RelayReserve = 13,
    RelayReservation = 14,
    CircuitIncoming = 15,
    CircuitConnect = 16,
    CircuitAccept = 17,
    CircuitStatus = 18,

    // Prueba de alcanzabilidad: otro nodo intenta conectarse de vuelta a mi puerto.
    DialBackRequest = 19,
    DialBackResult = 20,
    DialBackProbe = 21,

    // Dandelion++ (protocolo 3): transacción en fase privada ("tallo"), enviada a un solo par.
    StemTx = 22,
}

[Flags]
public enum NodeServices : ulong
{
    None = 0,
    /// <summary>El nodo ofrece relay de circuitos para pares sin puertos abiertos.</summary>
    Relay = 1,
    /// <summary>El nodo comprobó que recibe conexiones entrantes desde Internet.</summary>
    Reachable = 2,
}

public enum RelayStatus : byte
{
    Ok = 0,
    Refused = 1,
    NotRelay = 2,
    NoReservation = 3,
    Unavailable = 4,
}

public enum InvType : byte
{
    Block = 1,
    Tx = 2,
}

public sealed record PeerAddress(IPAddress Ip, int Port)
{
    public IPEndPoint EndPoint => new(Ip, Port);

    public string Key => ToString();

    public override string ToString() =>
        Ip.AddressFamily == AddressFamily.InterNetworkV6 && !Ip.IsIPv4MappedToIPv6 ? $"[{Ip}]:{Port}" : $"{Normalize(Ip)}:{Port}";

    public static IPAddress Normalize(IPAddress ip) => ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4() : ip;

    public static bool TryParse(string text, int defaultPort, out PeerAddress? address)
    {
        address = null;
        if (string.IsNullOrWhiteSpace(text)) return false;
        if (IPEndPoint.TryParse(text.Trim(), out var ep))
        {
            address = new PeerAddress(Normalize(ep.Address), ep.Port == 0 ? defaultPort : ep.Port);
            return address.Port is > 0 and <= 65535;
        }
        return false;
    }
}

public abstract record Message(MessageType Type);

/// <summary>
/// Primer mensaje de cada lado. Incluye la altura y el hash de la punta (solo informativos: nunca se
/// confía en ellos sin descargar y validar los bloques), la identidad del nodo y un desafío aleatorio
/// que el otro lado debe firmar en <see cref="HelloAckMessage"/>.
/// </summary>
public sealed record HelloMessage(
    uint ProtocolVersion, uint NetworkMagic, Hash256 GenesisHash, long BestHeight, BigInteger TotalWork,
    Hash256 TipHash, int ListenPort, NodeServices Services, PeerId NodeId, Hash256 Challenge,
    IPAddress ObservedIp, int ObservedPort, string UserAgent, long Timestamp) : Message(MessageType.Hello);

/// <summary>Firma Ed25519 del desafío recibido: prueba que el par controla la clave de su Peer ID.</summary>
public sealed record HelloAckMessage(byte[] Signature) : Message(MessageType.HelloAck);
public sealed record PingMessage(ulong Nonce) : Message(MessageType.Ping);
public sealed record PongMessage(ulong Nonce) : Message(MessageType.Pong);
public sealed record GetPeersMessage() : Message(MessageType.GetPeers);
public sealed record PeersMessage(IReadOnlyList<NodeAddress> Addresses) : Message(MessageType.Peers);
public sealed record RelayReserveMessage() : Message(MessageType.RelayReserve);
public sealed record RelayReservationMessage(RelayStatus Status, long ExpiresAt) : Message(MessageType.RelayReservation);
public sealed record CircuitIncomingMessage(Hash256 CircuitId) : Message(MessageType.CircuitIncoming);
public sealed record CircuitConnectMessage(PeerId Target) : Message(MessageType.CircuitConnect);
public sealed record CircuitAcceptMessage(Hash256 CircuitId) : Message(MessageType.CircuitAccept);
public sealed record CircuitStatusMessage(RelayStatus Status) : Message(MessageType.CircuitStatus);
public sealed record DialBackRequestMessage(Hash256 Nonce, int Port) : Message(MessageType.DialBackRequest);
public sealed record DialBackResultMessage(Hash256 Nonce, bool Success) : Message(MessageType.DialBackResult);
public sealed record DialBackProbeMessage(Hash256 Nonce) : Message(MessageType.DialBackProbe);
public sealed record GetBlocksMessage(IReadOnlyList<Hash256> Locator, Hash256 Stop) : Message(MessageType.GetBlocks);
public sealed record InvMessage(InvType Kind, IReadOnlyList<Hash256> Hashes) : Message(MessageType.Inv);
public sealed record GetDataMessage(InvType Kind, IReadOnlyList<Hash256> Hashes) : Message(MessageType.GetData);
public sealed record NotFoundMessage(InvType Kind, IReadOnlyList<Hash256> Hashes) : Message(MessageType.NotFound);
public sealed record BlockMessage(byte[] Payload) : Message(MessageType.Block);
public sealed record TxMessage(byte[] Payload) : Message(MessageType.Tx);
public sealed record StemTxMessage(byte[] Payload) : Message(MessageType.StemTx);

/// <summary>
/// Codificación del protocolo P2P de SHENT. Cada trama:
///   u32 magic de red | u8 tipo | u32 longitud | 4 bytes de checksum (SHA-256 del contenido) | contenido
/// </summary>
public static class Protocol
{
    public const uint Version = 3;
    public const uint MinVersion = 2;
    /// <summary>Primera versión que entiende <see cref="StemTxMessage"/>; a pares anteriores nunca se les envía.</summary>
    public const uint DandelionVersion = 3;
    public const int FrameHeaderSize = 13;
    /// <summary>Tamaño máximo de una trama antes de completar el handshake (Hello, HelloAck, relay, dial-back).</summary>
    public const int MaxPreHandshakePayload = 8 * 1024;
    public const int SignatureSize = 64;

    private const byte PeerFlagHasId = 1;
    private const byte PeerFlagCircuit = 2;
    public const int MaxInventory = 500;
    public const int MaxLocator = 101;
    public const int MaxPeersPerMessage = 100;
    public const int MaxUserAgentBytes = 64;

    public static int MaxPayload(NetworkParams network) => network.MaxBlockBytes + 1024;

    public static byte[] Frame(NetworkParams network, Message message)
    {
        var payload = EncodePayload(message);
        var checksum = SHA256.HashData(payload);
        var w = new ShentWriter(FrameHeaderSize + payload.Length);
        w.WriteU32(network.Magic)
         .WriteU8((byte)message.Type)
         .WriteU32((uint)payload.Length)
         .WriteBytes(checksum.AsSpan(0, 4))
         .WriteBytes(payload);
        return w.ToArray();
    }

    public static byte[] EncodePayload(Message message)
    {
        var w = new ShentWriter();
        switch (message)
        {
            case HelloMessage m:
                w.WriteU32(m.ProtocolVersion)
                 .WriteU32(m.NetworkMagic)
                 .WriteBytes(m.GenesisHash.Span)
                 .WriteVarInt((ulong)m.BestHeight)
                 .WriteVarBytes(m.TotalWork.ToByteArray(isUnsigned: true, isBigEndian: true))
                 .WriteBytes(m.TipHash.Span)
                 .WriteU16((ushort)m.ListenPort)
                 .WriteU64((ulong)m.Services)
                 .WriteBytes(m.NodeId.PublicKey)
                 .WriteBytes(m.Challenge.Span)
                 .WriteBytes(m.ObservedIp.MapToIPv6().GetAddressBytes())
                 .WriteU16((ushort)m.ObservedPort)
                 .WriteString(SanitizeUserAgent(m.UserAgent))
                 .WriteI64(m.Timestamp);
                break;
            case HelloAckMessage m:
                if (m.Signature.Length != SignatureSize) throw new ArgumentException("Firma de handshake inválida.");
                w.WriteBytes(m.Signature);
                break;
            case GetPeersMessage or RelayReserveMessage:
                break;
            case PingMessage m:
                w.WriteU64(m.Nonce);
                break;
            case PongMessage m:
                w.WriteU64(m.Nonce);
                break;
            case PeersMessage m:
                w.WriteVarInt((ulong)m.Addresses.Count);
                foreach (var a in m.Addresses)
                {
                    var flags = (byte)((a.PeerId is not null ? PeerFlagHasId : 0) | (a.CircuitTarget is not null && a.PeerId is not null ? PeerFlagCircuit : 0));
                    w.WriteU8(flags)
                     .WriteBytes(a.Endpoint.Ip.MapToIPv6().GetAddressBytes())
                     .WriteU16((ushort)a.Endpoint.Port);
                    if (a.PeerId is not null) w.WriteBytes(a.PeerId.PublicKey);
                    if ((flags & PeerFlagCircuit) != 0) w.WriteBytes(a.CircuitTarget!.PublicKey);
                }
                break;
            case RelayReservationMessage m:
                w.WriteU8((byte)m.Status).WriteI64(m.ExpiresAt);
                break;
            case CircuitIncomingMessage m:
                w.WriteBytes(m.CircuitId.Span);
                break;
            case CircuitConnectMessage m:
                w.WriteBytes(m.Target.PublicKey);
                break;
            case CircuitAcceptMessage m:
                w.WriteBytes(m.CircuitId.Span);
                break;
            case CircuitStatusMessage m:
                w.WriteU8((byte)m.Status);
                break;
            case DialBackRequestMessage m:
                w.WriteBytes(m.Nonce.Span).WriteU16((ushort)m.Port);
                break;
            case DialBackResultMessage m:
                w.WriteBytes(m.Nonce.Span).WriteU8(m.Success ? (byte)1 : (byte)0);
                break;
            case DialBackProbeMessage m:
                w.WriteBytes(m.Nonce.Span);
                break;
            case GetBlocksMessage m:
                w.WriteVarInt((ulong)m.Locator.Count);
                foreach (var h in m.Locator) w.WriteBytes(h.Span);
                w.WriteBytes(m.Stop.Span);
                break;
            case InvMessage m:
                WriteInventory(w, m.Kind, m.Hashes);
                break;
            case GetDataMessage m:
                WriteInventory(w, m.Kind, m.Hashes);
                break;
            case NotFoundMessage m:
                WriteInventory(w, m.Kind, m.Hashes);
                break;
            case BlockMessage m:
                w.WriteBytes(m.Payload);
                break;
            case TxMessage m:
                w.WriteBytes(m.Payload);
                break;
            case StemTxMessage m:
                w.WriteBytes(m.Payload);
                break;
            default:
                throw new ArgumentException("Mensaje desconocido.");
        }
        return w.ToArray();
    }

    public static string SanitizeUserAgent(string userAgent)
    {
        var ascii = new string(userAgent.Where(c => c is >= ' ' and <= '~').ToArray());
        return ascii.Length > MaxUserAgentBytes ? ascii[..MaxUserAgentBytes] : ascii;
    }

    private static void WriteInventory(ShentWriter w, InvType kind, IReadOnlyList<Hash256> hashes)
    {
        w.WriteU8((byte)kind).WriteVarInt((ulong)hashes.Count);
        foreach (var h in hashes) w.WriteBytes(h.Span);
    }

    private static RelayStatus ReadRelayStatus(ShentReader r)
    {
        var status = r.ReadU8();
        if (status > (byte)RelayStatus.Unavailable) throw new FormatException("Estado de relay desconocido.");
        return (RelayStatus)status;
    }

    /// <summary>Mensajes válidos como primera trama de una conexión TCP entrante.</summary>
    public static bool IsConnectionOpener(MessageType type) =>
        type is MessageType.Hello or MessageType.CircuitConnect or MessageType.CircuitAccept or MessageType.DialBackProbe;

    private static (InvType, List<Hash256>) ReadInventory(ShentReader r)
    {
        var kind = r.ReadU8();
        if (kind is not ((byte)InvType.Block or (byte)InvType.Tx)) throw new FormatException("Tipo de inventario desconocido.");
        var count = r.ReadCount(MaxInventory);
        var hashes = new List<Hash256>(count);
        for (var i = 0; i < count; i++) hashes.Add(r.ReadHash());
        return ((InvType)kind, hashes);
    }

    public static Message Decode(MessageType type, byte[] payload)
    {
        var r = new ShentReader(payload);
        Message message;
        switch (type)
        {
            case MessageType.Hello:
                message = new HelloMessage(
                    r.ReadU32(), r.ReadU32(), r.ReadHash(), r.ReadVarLong(),
                    new BigInteger(r.ReadVarBytes(33), isUnsigned: true, isBigEndian: true),
                    r.ReadHash(), r.ReadU16(), (NodeServices)r.ReadU64(), new PeerId(r.ReadBytes(PeerId.Size)), r.ReadHash(),
                    PeerAddress.Normalize(new IPAddress(r.ReadBytes(16))), r.ReadU16(),
                    r.ReadString(MaxUserAgentBytes), r.ReadI64());
                break;
            case MessageType.HelloAck:
                message = new HelloAckMessage(r.ReadBytes(SignatureSize));
                break;
            case MessageType.RelayReserve:
                message = new RelayReserveMessage();
                break;
            case MessageType.RelayReservation:
                message = new RelayReservationMessage(ReadRelayStatus(r), r.ReadI64());
                break;
            case MessageType.CircuitIncoming:
                message = new CircuitIncomingMessage(r.ReadHash());
                break;
            case MessageType.CircuitConnect:
                message = new CircuitConnectMessage(new PeerId(r.ReadBytes(PeerId.Size)));
                break;
            case MessageType.CircuitAccept:
                message = new CircuitAcceptMessage(r.ReadHash());
                break;
            case MessageType.CircuitStatus:
                message = new CircuitStatusMessage(ReadRelayStatus(r));
                break;
            case MessageType.DialBackRequest:
                message = new DialBackRequestMessage(r.ReadHash(), r.ReadU16());
                break;
            case MessageType.DialBackResult:
                message = new DialBackResultMessage(r.ReadHash(), r.ReadU8() switch
                {
                    0 => false,
                    1 => true,
                    _ => throw new FormatException("Resultado de dial-back inválido."),
                });
                break;
            case MessageType.DialBackProbe:
                message = new DialBackProbeMessage(r.ReadHash());
                break;
            case MessageType.Ping:
                message = new PingMessage(r.ReadU64());
                break;
            case MessageType.Pong:
                message = new PongMessage(r.ReadU64());
                break;
            case MessageType.GetPeers:
                message = new GetPeersMessage();
                break;
            case MessageType.Peers:
            {
                var count = r.ReadCount(MaxPeersPerMessage);
                var list = new List<NodeAddress>(count);
                for (var i = 0; i < count; i++)
                {
                    var flags = r.ReadU8();
                    if ((flags & ~(PeerFlagHasId | PeerFlagCircuit)) != 0 || flags == PeerFlagCircuit)
                    {
                        throw new FormatException("Banderas de dirección inválidas.");
                    }
                    var ip = PeerAddress.Normalize(new IPAddress(r.ReadBytes(16)));
                    var endpoint = new PeerAddress(ip, r.ReadU16());
                    var id = (flags & PeerFlagHasId) != 0 ? new PeerId(r.ReadBytes(PeerId.Size)) : null;
                    var target = (flags & PeerFlagCircuit) != 0 ? new PeerId(r.ReadBytes(PeerId.Size)) : null;
                    list.Add(new NodeAddress(endpoint, id, target));
                }
                message = new PeersMessage(list);
                break;
            }
            case MessageType.GetBlocks:
            {
                var count = r.ReadCount(MaxLocator);
                var locator = new List<Hash256>(count);
                for (var i = 0; i < count; i++) locator.Add(r.ReadHash());
                message = new GetBlocksMessage(locator, r.ReadHash());
                break;
            }
            case MessageType.Inv:
            {
                var (kind, hashes) = ReadInventory(r);
                message = new InvMessage(kind, hashes);
                break;
            }
            case MessageType.GetData:
            {
                var (kind, hashes) = ReadInventory(r);
                message = new GetDataMessage(kind, hashes);
                break;
            }
            case MessageType.NotFound:
            {
                var (kind, hashes) = ReadInventory(r);
                message = new NotFoundMessage(kind, hashes);
                break;
            }
            case MessageType.Block:
                return new BlockMessage(payload);
            case MessageType.Tx:
                return new TxMessage(payload);
            case MessageType.StemTx:
                return new StemTxMessage(payload);
            default:
                throw new FormatException($"Tipo de mensaje desconocido: {(byte)type}.");
        }
        r.EnsureEnd();
        return message;
    }
}
