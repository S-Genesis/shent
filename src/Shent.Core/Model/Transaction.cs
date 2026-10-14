using NSec.Cryptography;
using Shent.Core.Consensus;
using Shent.Core.Crypto;
using Shent.Core.Encoding;

namespace Shent.Core.Model;

public enum TxKind : byte
{
    Coinbase = 0,
    Transfer = 1,
}

/// <summary>
/// Transacción SHENT (modelo de cuentas con nonce secuencial).
///
/// Formato binario canónico v1:
///   u8 versión | u8 tipo | u32 magic de red
///   Coinbase:  varint altura | 20 bytes destinatario | i64 importe | varbytes datos extra (≤ 64)
///   Transfer:  32 bytes clave pública | 20 bytes destinatario | i64 importe | i64 comisión |
///              varint nonce | varint altura de expiración (0 = sin expiración) | varbytes memo (≤ 80)
///              + 64 bytes de firma Ed25519 (fuera del cuerpo firmado)
///
/// La firma cubre "SHENT-TX-V1" + cuerpo completo, incluido el magic de red: una transacción
/// válida en una red no puede reproducirse en otra. El identificador es SHA-256d del cuerpo
/// (sin la firma), por lo que no es maleable.
/// </summary>
public sealed class Transaction
{
    public const byte CurrentVersion = 1;
    public const int MaxExtraBytes = 64;
    public const int MaxMemoBytes = 80;
    public const int MaxSerializedSize = 512;
    private static readonly byte[] SigningDomain = "SHENT-TX-V1"u8.ToArray();

    private Hash256? _id;
    private byte[]? _body;

    public byte Version { get; init; } = CurrentVersion;
    public TxKind Kind { get; init; }
    public uint NetworkMagic { get; init; }

    public long Height { get; init; }
    public byte[] ExtraData { get; init; } = [];

    public byte[] SenderPublicKey { get; init; } = [];
    public long Fee { get; init; }
    public long Nonce { get; init; }
    public long ExpiresAtHeight { get; init; }
    public byte[] Memo { get; init; } = [];
    public byte[] Signature { get; set; } = [];

    public required Address Recipient { get; init; }
    public long Amount { get; init; }

    public bool IsCoinbase => Kind == TxKind.Coinbase;

    public Address Sender => IsCoinbase
        ? throw new InvalidOperationException("La coinbase no tiene remitente.")
        : Address.FromPublicKey(SenderPublicKey);

    public Hash256 Id => _id ??= Hash256.Sha256d(Body);

    private byte[] Body => _body ??= WriteBody();

    private byte[] WriteBody()
    {
        var w = new ShentWriter(160);
        w.WriteU8(Version).WriteU8((byte)Kind).WriteU32(NetworkMagic);
        if (IsCoinbase)
        {
            w.WriteVarInt((ulong)Height)
             .WriteBytes(Recipient.Hash)
             .WriteI64(Amount)
             .WriteVarBytes(ExtraData);
        }
        else
        {
            w.WriteBytes(SenderPublicKey)
             .WriteBytes(Recipient.Hash)
             .WriteI64(Amount)
             .WriteI64(Fee)
             .WriteVarInt((ulong)Nonce)
             .WriteVarInt((ulong)ExpiresAtHeight)
             .WriteVarBytes(Memo);
        }
        return w.ToArray();
    }

    public byte[] SigningBytes()
    {
        var body = Body;
        var result = new byte[SigningDomain.Length + body.Length];
        SigningDomain.CopyTo(result, 0);
        body.CopyTo(result, SigningDomain.Length);
        return result;
    }

    public byte[] Serialize()
    {
        if (IsCoinbase) return Body.ToArray();
        var body = Body;
        var result = new byte[body.Length + Ed25519Keys.SignatureSize];
        body.CopyTo(result, 0);
        Signature.AsSpan().CopyTo(result.AsSpan(body.Length));
        return result;
    }

    public int SerializedSize => IsCoinbase ? Body.Length : Body.Length + Ed25519Keys.SignatureSize;

    public bool VerifySignature()
    {
        if (IsCoinbase) return false;
        return Ed25519Keys.Verify(SenderPublicKey, SigningBytes(), Signature);
    }

    public void Sign(Key key)
    {
        if (IsCoinbase) throw new InvalidOperationException("La coinbase no se firma.");
        var pub = Ed25519Keys.PublicKeyOf(key);
        if (!pub.AsSpan().SequenceEqual(SenderPublicKey))
        {
            throw new InvalidOperationException("La clave no corresponde al remitente.");
        }
        Signature = Ed25519Keys.Sign(key, SigningBytes());
    }

    public static Transaction CreateTransfer(
        Key senderKey, uint networkMagic, Address recipient, long amount, long fee, long nonce,
        long expiresAtHeight = 0, byte[]? memo = null)
    {
        var tx = new Transaction
        {
            Kind = TxKind.Transfer,
            NetworkMagic = networkMagic,
            SenderPublicKey = Ed25519Keys.PublicKeyOf(senderKey),
            Recipient = recipient,
            Amount = amount,
            Fee = fee,
            Nonce = nonce,
            ExpiresAtHeight = expiresAtHeight,
            Memo = memo ?? [],
        };
        tx.Sign(senderKey);
        return tx;
    }

    public static Transaction CreateCoinbase(uint networkMagic, long height, Address recipient, long amount, byte[] extra)
    {
        return new Transaction
        {
            Kind = TxKind.Coinbase,
            NetworkMagic = networkMagic,
            Height = height,
            Recipient = recipient,
            Amount = amount,
            ExtraData = extra,
        };
    }

    public static Transaction Deserialize(ReadOnlyMemory<byte> data)
    {
        if (data.Length > MaxSerializedSize)
        {
            throw new FormatException("Transacción demasiado grande.");
        }
        var r = new ShentReader(data);
        var tx = Read(r);
        r.EnsureEnd();
        return tx;
    }

    public static Transaction Read(ShentReader r)
    {
        var version = r.ReadU8();
        if (version != CurrentVersion)
        {
            throw new FormatException($"Versión de transacción no soportada: {version}.");
        }
        var kindByte = r.ReadU8();
        if (kindByte > (byte)TxKind.Transfer)
        {
            throw new FormatException("Tipo de transacción desconocido.");
        }
        var kind = (TxKind)kindByte;
        var magic = r.ReadU32();
        if (kind == TxKind.Coinbase)
        {
            var height = r.ReadVarLong();
            var recipient = Address.FromHash(r.ReadBytes(Address.HashSize));
            var amount = r.ReadI64();
            var extra = r.ReadVarBytes(MaxExtraBytes);
            return new Transaction
            {
                Version = version,
                Kind = kind,
                NetworkMagic = magic,
                Height = height,
                Recipient = recipient,
                Amount = amount,
                ExtraData = extra,
            };
        }

        var pub = r.ReadBytes(Ed25519Keys.PublicKeySize);
        var to = Address.FromHash(r.ReadBytes(Address.HashSize));
        var value = r.ReadI64();
        var fee = r.ReadI64();
        var nonce = r.ReadVarLong();
        var expires = r.ReadVarLong();
        var memo = r.ReadVarBytes(MaxMemoBytes);
        var signature = r.ReadBytes(Ed25519Keys.SignatureSize);
        return new Transaction
        {
            Version = version,
            Kind = kind,
            NetworkMagic = magic,
            SenderPublicKey = pub,
            Recipient = to,
            Amount = value,
            Fee = fee,
            Nonce = nonce,
            ExpiresAtHeight = expires,
            Memo = memo,
            Signature = signature,
        };
    }

    public string MemoText => Memo.Length == 0 ? string.Empty : System.Text.Encoding.UTF8.GetString(Memo);
}
