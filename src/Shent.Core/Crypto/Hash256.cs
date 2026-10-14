using System.Buffers.Binary;
using System.Security.Cryptography;

namespace Shent.Core.Crypto;

/// <summary>Valor inmutable de 32 bytes (hash de bloque, de transacción o raíz Merkle).</summary>
public readonly struct Hash256 : IEquatable<Hash256>
{
    public const int Size = 32;
    private readonly byte[]? _bytes;

    public static readonly Hash256 Zero = new(new byte[Size]);

    public Hash256(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length != Size)
        {
            throw new ArgumentException($"Un Hash256 requiere exactamente {Size} bytes.", nameof(bytes));
        }
        _bytes = bytes.ToArray();
    }

    public ReadOnlySpan<byte> Span => _bytes ?? Zero._bytes!;

    public byte[] ToArray() => Span.ToArray();

    public bool IsZero
    {
        get
        {
            foreach (var b in Span)
            {
                if (b != 0) return false;
            }
            return true;
        }
    }

    public static Hash256 Sha256(ReadOnlySpan<byte> data) => new(SHA256.HashData(data));

    /// <summary>SHA-256 aplicado dos veces; se usa para identificadores de bloques y transacciones.</summary>
    public static Hash256 Sha256d(ReadOnlySpan<byte> data)
    {
        Span<byte> first = stackalloc byte[Size];
        SHA256.HashData(data, first);
        return new Hash256(SHA256.HashData(first));
    }

    public static Hash256 Parse(string hex)
    {
        if (hex is null || hex.Length != Size * 2)
        {
            throw new FormatException("El hash debe tener 64 caracteres hexadecimales.");
        }
        return new Hash256(Convert.FromHexString(hex));
    }

    public static bool TryParse(string? hex, out Hash256 hash)
    {
        hash = default;
        if (hex is null || hex.Length != Size * 2) return false;
        try
        {
            hash = new Hash256(Convert.FromHexString(hex));
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    public bool Equals(Hash256 other) => Span.SequenceEqual(other.Span);

    public override bool Equals(object? obj) => obj is Hash256 other && Equals(other);

    public override int GetHashCode() => BinaryPrimitives.ReadInt32LittleEndian(Span);

    public static bool operator ==(Hash256 left, Hash256 right) => left.Equals(right);

    public static bool operator !=(Hash256 left, Hash256 right) => !left.Equals(right);

    public override string ToString() => Convert.ToHexString(Span).ToLowerInvariant();

    public string ToShortString() => $"{ToString()[..8]}…{ToString()[^6..]}";
}
