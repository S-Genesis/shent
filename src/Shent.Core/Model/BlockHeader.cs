using System.Buffers.Binary;
using Shent.Core.Crypto;
using Shent.Core.Encoding;

namespace Shent.Core.Model;

/// <summary>
/// Cabecera de bloque de 100 bytes:
///   u32 versión | u32 magic de red | i64 altura | 32 bytes hash previo | 32 bytes raíz Merkle |
///   i64 marca de tiempo (Unix, segundos) | u32 objetivo compacto | u64 nonce
/// El identificador del bloque es SHA-256d(cabecera); la prueba de trabajo es Argon2id(cabecera) ≤ objetivo.
/// </summary>
public sealed class BlockHeader
{
    public const int Size = 100;
    public const int NonceOffset = 92;
    public const uint CurrentVersion = 1;

    public uint Version { get; init; } = CurrentVersion;
    public uint NetworkMagic { get; init; }
    public long Height { get; init; }
    public Hash256 PrevHash { get; init; }
    public Hash256 MerkleRoot { get; init; }
    public long Timestamp { get; init; }
    public uint Bits { get; init; }
    public ulong Nonce { get; init; }

    public byte[] Serialize()
    {
        var w = new ShentWriter(Size);
        w.WriteU32(Version)
         .WriteU32(NetworkMagic)
         .WriteI64(Height)
         .WriteBytes(PrevHash.Span)
         .WriteBytes(MerkleRoot.Span)
         .WriteI64(Timestamp)
         .WriteU32(Bits)
         .WriteU64(Nonce);
        return w.ToArray();
    }

    public Hash256 ComputeHash() => Hash256.Sha256d(Serialize());

    public static BlockHeader Read(ShentReader r)
    {
        return new BlockHeader
        {
            Version = r.ReadU32(),
            NetworkMagic = r.ReadU32(),
            Height = r.ReadI64(),
            PrevHash = r.ReadHash(),
            MerkleRoot = r.ReadHash(),
            Timestamp = r.ReadI64(),
            Bits = r.ReadU32(),
            Nonce = r.ReadU64(),
        };
    }

    public static BlockHeader Deserialize(ReadOnlyMemory<byte> data)
    {
        var r = new ShentReader(data);
        var header = Read(r);
        r.EnsureEnd();
        return header;
    }

    public BlockHeader WithNonce(ulong nonce) => new()
    {
        Version = Version,
        NetworkMagic = NetworkMagic,
        Height = Height,
        PrevHash = PrevHash,
        MerkleRoot = MerkleRoot,
        Timestamp = Timestamp,
        Bits = Bits,
        Nonce = nonce,
    };

    public static void WriteNonce(Span<byte> headerBytes, ulong nonce)
    {
        BinaryPrimitives.WriteUInt64LittleEndian(headerBytes.Slice(NonceOffset, 8), nonce);
    }
}
